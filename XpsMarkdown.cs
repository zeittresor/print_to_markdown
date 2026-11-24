using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PrintToMarkdown
{
    internal sealed class XpsMarkdown
    {
        private const int XmlLimit = 16 * 1024 * 1024;
        internal const int JobLimit = 128 * 1024 * 1024;
        private static readonly Regex ListMarker = new Regex(@"^(?:(?<bullet>[•◦▪●‣*+-])|(?<number>\d{1,9})[.)])\s+(?<text>\S.*)$");
        private static readonly Regex Whitespace = new Regex(@"[\s\p{Cc}\p{Z}]+");

        internal sealed class Document
        {
            internal string Title;
            internal string Markdown;
        }

        private sealed class Run
        {
            internal double Left, Right, Baseline, Size;
            internal string Text;
        }

        private sealed class Row
        {
            internal readonly List<Run> Cells = new List<Run>();
            internal double Baseline { get { return Cells[0].Baseline; } }
            internal double Size { get { return Cells.Max(run => run.Size); } }
            internal string Text { get { return String.Join(" ", Cells.Select(run => run.Text.Trim()).Where(text => text.Length != 0)); } }
        }

        private struct Matrix
        {
            internal double A, B, C, D, X, Y;
            internal Matrix(double a, double b, double c, double d, double x, double y)
            {
                A = a; B = b; C = c; D = d; X = x; Y = y;
            }

            internal static readonly Matrix Identity = new Matrix(1, 0, 0, 1, 0, 0);

            internal Matrix Then(Matrix outer)
            {
                return new Matrix(
                    A * outer.A + B * outer.C, A * outer.B + B * outer.D,
                    C * outer.A + D * outer.C, C * outer.B + D * outer.D,
                    X * outer.A + Y * outer.C + outer.X, X * outer.B + Y * outer.D + outer.Y);
            }
        }

        internal static Document Convert(Stream input, string defaultTitle)
        {
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
            {
                var parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        if (parts.ContainsKey(entry.FullName))
                            throw new InvalidDataException("Das Paket enthält doppelte Dokumentteile.");
                        parts.Add(entry.FullName, entry);
                    }
                }

                var relations = Read(parts, "_rels/.rels").Elements().ToList();
                var fixedDocument = relations.FirstOrDefault(item =>
                    Attribute(item, "Type").EndsWith("/fixedrepresentation", StringComparison.OrdinalIgnoreCase));
                if (fixedDocument == null)
                    throw new InvalidDataException("Das Paket enthält kein XPS- oder OpenXPS-Dokument.");

                string title = defaultTitle;
                var metadata = relations.FirstOrDefault(item =>
                    Attribute(item, "Type").EndsWith("/metadata/core-properties", StringComparison.OrdinalIgnoreCase));
                if (String.IsNullOrWhiteSpace(title) && metadata != null)
                {
                    var core = Read(parts, Resolve("", Attribute(metadata, "Target")));
                    var name = core.Elements().FirstOrDefault(element => element.Name.LocalName == "title");
                    if (name != null)
                        title = name.Value;
                }

                string sequencePart = Resolve("", Attribute(fixedDocument, "Target"));
                var sequence = Read(parts, sequencePart);
                var pages = new List<List<Row>>();
                var heights = new List<double>();
                var sizes = new Dictionary<double, long>();
                foreach (var reference in sequence.Elements().Where(node => node.Name.LocalName == "DocumentReference"))
                {
                    string documentPart = Resolve(sequencePart, Attribute(reference, "Source"));
                    var document = Read(parts, documentPart);
                    foreach (var content in document.Elements().Where(node => node.Name.LocalName == "PageContent"))
                    {
                        if (pages.Count >= 1000)
                            throw new InvalidDataException("Der Auftrag enthält mehr als 1.000 Seiten.");
                        var page = Read(parts, Resolve(documentPart, Attribute(content, "Source")));
                        var runs = new List<Run>();
                        Collect(page, Matrix.Identity, runs);
                        heights.Add(Number(Attribute(page, "Height")));
                        pages.Add(ToRows(runs));
                        foreach (var run in runs)
                        {
                            if (!sizes.ContainsKey(run.Size))
                                sizes[run.Size] = 0;
                            sizes[run.Size] += run.Text.Trim().Length;
                        }
                    }
                }

                if (pages.Count == 0)
                    throw new InvalidDataException("Das XPS-Dokument enthält keine Seiten.");
                double bodySize = sizes.Count == 0 ? 0 : sizes.OrderByDescending(pair => pair.Value).First().Key;
                var headings = sizes.Keys.Where(size => size >= bodySize + Math.Max(2, bodySize * 0.18))
                    .OrderByDescending(size => size).Select((size, index) => new { size, level = Math.Min(4, index + 1) })
                    .ToDictionary(pair => pair.size, pair => pair.level);
                return new Document { Title = title, Markdown = Render(pages, heights, headings) };
            }
        }

        private static XElement Read(Dictionary<string, ZipArchiveEntry> parts, string name)
        {
            ZipArchiveEntry entry;
            XDocument document;
            if (!parts.TryGetValue(name, out entry))
                throw new InvalidDataException("Ein benötigter XPS-Dokumentteil fehlt.");
            if (entry.Length > XmlLimit)
                throw new InvalidDataException("Ein XPS-Dokumentteil überschreitet 16 MiB.");
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = XmlLimit
            };
            using (var stream = entry.Open())
            using (var xml = XmlReader.Create(stream, settings))
                document = XDocument.Load(xml, LoadOptions.None);
            if (document.Root == null)
                throw new InvalidDataException("Ein XPS-Dokumentteil ist leer.");
            return document.Root;
        }

        private static string Resolve(string from, string target)
        {
            Uri resolved;
            if (String.IsNullOrWhiteSpace(target) ||
                !Uri.TryCreate(new Uri("https://xps.invalid/" + from), target, out resolved) ||
                resolved.Host != "xps.invalid" || resolved.Scheme != "https" ||
                resolved.Query.Length != 0 || resolved.Fragment.Length != 0)
                throw new InvalidDataException("Ungültiger Verweis im XPS-Paket.");
            return Uri.UnescapeDataString(resolved.AbsolutePath).TrimStart('/');
        }

        private static string Attribute(XElement element, string name)
        {
            return (string)element.Attribute(name) ?? "";
        }

        private static double Number(string value)
        {
            double result;
            return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                && !Double.IsInfinity(result) && !Double.IsNaN(result) ? result : 0;
        }

        private static Matrix Transform(XElement element)
        {
            string value = Attribute(element, "RenderTransform");
            if (value.Length == 0)
            {
                var property = element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName == element.Name.LocalName + ".RenderTransform");
                var matrix = property == null ? null : property.Elements().FirstOrDefault(child =>
                    child.Name.LocalName == "MatrixTransform");
                if (matrix != null)
                    value = Attribute(matrix, "Matrix");
            }
            string[] values = Regex.Split(value.Trim(), @"[,\s]+");
            return values.Length == 6 ? new Matrix(
                Number(values[0]), Number(values[1]), Number(values[2]),
                Number(values[3]), Number(values[4]), Number(values[5])) : Matrix.Identity;
        }

        private static void Collect(XElement root, Matrix initial, List<Run> output)
        {
            var pending = new Stack<KeyValuePair<XElement, Matrix>>();
            pending.Push(new KeyValuePair<XElement, Matrix>(root, initial));
            while (pending.Count != 0)
            {
                var item = pending.Pop();
                var element = item.Key;
                Matrix transform = Transform(element).Then(item.Value);
                string type = element.Name.LocalName;
                double opacity;
                if (Double.TryParse(Attribute(element, "Opacity"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out opacity) && opacity == 0)
                    continue;
                if (type == "Glyphs")
                {
                    string text = Whitespace.Replace(Attribute(element, "UnicodeString"), " ");
                    if (text.StartsWith("{}", StringComparison.Ordinal))
                        text = text.Substring(2);
                    double size = Number(Attribute(element, "FontRenderingEmSize"));
                    if (String.IsNullOrWhiteSpace(text) || size <= 0)
                        continue;

                    double x = Number(Attribute(element, "OriginX"));
                    double y = Number(Attribute(element, "OriginY"));
                    double left = x * transform.A + y * transform.C + transform.X;
                    double baseline = x * transform.B + y * transform.D + transform.Y;
                    double width = EstimateWidth(text, size, Attribute(element, "Indices"));
                    int bidi;
                    bool rightToLeft = Int32.TryParse(Attribute(element, "BidiLevel"), out bidi) && bidi % 2 != 0;
                    double end = left + width * transform.A * (rightToLeft ? -1 : 1);
                    output.Add(new Run
                    {
                        Text = text, Size = Math.Round(Math.Max(1, size * Math.Sqrt(transform.C * transform.C + transform.D * transform.D)) * 2) / 2,
                        Left = Math.Min(left, end), Right = Math.Max(left, end), Baseline = baseline
                    });
                }
                else if (type == "FixedPage" || type == "Canvas")
                {
                    foreach (var child in element.Elements().Reverse())
                        pending.Push(new KeyValuePair<XElement, Matrix>(child, transform));
                }
            }
        }

        private static double EstimateWidth(string text, double size, string indices)
        {
            if (indices.Length == 0)
                return text.Sum(character => Char.IsWhiteSpace(character) ? 0.27 : 0.49) * size;

            double width = 0;
            foreach (string glyph in indices.Split(';'))
            {
                string[] metrics = glyph.Split(',');
                width += metrics.Length > 1 && metrics[1].Length > 0 ? Number(metrics[1]) / 100 : 0.49;
            }
            return width > 0 ? width * size : text.Length * size * 0.49;
        }

        private static List<Row> ToRows(List<Run> runs)
        {
            var rows = new List<Row>();
            var line = new List<Run>();
            double baseline = 0;
            double tolerance = 0;
            foreach (var run in runs.OrderBy(run => run.Baseline))
            {
                if (line.Count > 0 && run.Baseline - baseline > Math.Max(tolerance, run.Size * 0.22))
                {
                    rows.Add(MergeLine(line));
                    line.Clear();
                }
                if (line.Count == 0)
                {
                    baseline = run.Baseline;
                    tolerance = Math.Max(1.2, run.Size * 0.22);
                }
                line.Add(run);
            }
            if (line.Count > 0)
                rows.Add(MergeLine(line));
            return rows;
        }

        private static Row MergeLine(List<Run> line)
        {
            var result = new Row();
            Run cell = null;
            foreach (var run in line.OrderBy(run => run.Left))
            {
                double gap = cell == null ? 0 : run.Left - cell.Right;
                if (cell == null || gap >= 2.1 * run.Size)
                {
                    cell = new Run
                    {
                        Left = run.Left, Right = run.Right, Baseline = run.Baseline, Size = run.Size, Text = run.Text
                    };
                    result.Cells.Add(cell);
                    continue;
                }
                if (gap > 0.13 * run.Size && !Char.IsWhiteSpace(cell.Text[cell.Text.Length - 1]) && !Char.IsWhiteSpace(run.Text[0]))
                    cell.Text += " ";
                cell.Text += run.Text;
                cell.Right = Math.Max(cell.Right, run.Right);
                cell.Size = Math.Max(cell.Size, run.Size);
            }
            return result;
        }

        private static string Escape(string text)
        {
            return Regex.Replace(text, @"([\\`*_\[\]<>|&])", @"\$1");
        }

        private static string EscapeLine(string text)
        {
            string escaped = Escape(text);
            return Regex.IsMatch(escaped, @"^(?:[#=+\-](?:\s|[=+\-]|$))") ? "\\" + escaped : escaped;
        }

        private static string Render(List<List<Row>> pages, List<double> heights, Dictionary<double, int> headings)
        {
            var margins = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < pages.Count; index++)
            {
                foreach (string key in pages[index].Select(line => MarginKey(line, heights[index]))
                    .Where(key => key != null).Distinct())
                {
                    if (!margins.ContainsKey(key))
                        margins[key] = 0;
                    margins[key]++;
                }
            }
            var result = new StringBuilder();
            for (int index = 0; index < pages.Count; index++)
            {
                result.AppendFormat("<!-- Seite {0} -->\n\n", index + 1);
                var rows = pages[index].Where(row =>
                {
                    var key = MarginKey(row, heights[index]);
                    return key == null || margins[key] < Math.Max(2, pages.Count * 0.65);
                }).ToList();
                if (rows.Count == 0)
                    rows = pages[index];
                if (rows.Count == 0)
                    result.Append("*Auf dieser Seite wurde kein eingebetteter Text gefunden.*\n\n");
                else
                    RenderRows(result, rows, headings);
            }
            return result.ToString().TrimEnd() + "\n";
        }

        private static string MarginKey(Row row, double height)
        {
            if (height <= 0)
                return null;
            if (row.Baseline < height * 0.065)
                return "top " + row.Text;
            return row.Baseline > height * 0.935 ? "bottom " + row.Text : null;
        }

        private static bool AlignedTableRow(Row first, Row next)
        {
            return first.Cells.Count >= 3 && first.Cells.Count == next.Cells.Count &&
                first.Cells.Zip(next.Cells, (a, b) => Math.Abs(a.Left - b.Left) < Math.Max(3, a.Size * 0.55)).All(match => match);
        }

        private static void RenderRows(StringBuilder result, List<Row> rows, Dictionary<double, int> headings)
        {
            Row previous = null;
            string state = "";
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                if (row.Text.Length == 0)
                    continue;
                int end = i + 1;
                while (end < rows.Count && rows[end].Baseline - rows[end - 1].Baseline <= 2.2 * row.Size
                    && AlignedTableRow(row, rows[end]))
                    end++;
                if (end >= i + 3 && rows.Skip(i).Take(end - i).Any(item =>
                    item.Cells.Skip(1).Any(cell => Regex.IsMatch(cell.Text.Trim(), @"^[-+]?\d[\d ,.:%€$]*$"))))
                {
                    if (previous != null)
                        result.Append("\n\n");
                    result.Append("| " + String.Join(" | ", Enumerable.Repeat("", row.Cells.Count)) + " |\n");
                    result.Append("| " + String.Join(" | ", Enumerable.Repeat("---", row.Cells.Count)) + " |");
                    for (; i < end; i++)
                        result.Append("\n| " + String.Join(" | ", rows[i].Cells.Select(cell => Escape(cell.Text.Trim()))) + " |");
                    i--;
                    previous = rows[i];
                    state = "table";
                    continue;
                }

                string text = row.Text;
                var list = ListMarker.Match(text);
                double gap = previous == null ? 0 : row.Baseline - previous.Baseline;
                int level;
                if (!list.Success && text.Length < 180 && headings.TryGetValue(row.Size, out level))
                {
                    if (previous != null)
                        result.Append("\n\n");
                    result.Append(new string('#', level) + " " + Escape(text));
                    state = "heading";
                }
                else if (list.Success)
                {
                    if (previous != null)
                        result.Append(state == "list" && gap < 1.9 * row.Size ? "\n" : "\n\n");
                    string marker = list.Groups["number"].Success ? list.Groups["number"].Value + "." : "-";
                    result.Append(marker + " " + Escape(list.Groups["text"].Value));
                    state = "list";
                }
                else
                {
                    bool continuation = previous != null && (state == "text" || state == "list")
                        && gap < Math.Max(row.Size, previous.Size) * 1.55
                        && (state == "text" || row.Cells[0].Left > previous.Cells[0].Left + 2);
                    if (previous != null)
                        result.Append(continuation ? " " : "\n\n");
                    result.Append(continuation ? Escape(text) : EscapeLine(text));
                    if (!continuation)
                        state = "text";
                }
                previous = row;
            }
            result.Append("\n\n");
        }
    }
}
