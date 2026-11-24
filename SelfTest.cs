using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PrintToMarkdown
{
    internal static class SelfTest
    {
        internal static int Run(string logPath)
        {
            try
            {
                Verify("http://schemas.microsoft.com/xps/2005/06");
                Verify("http://schemas.openxps.org/oxps/v1.0");
                Log(logPath, "XPS and OpenXPS conversion tests passed.");
                return 0;
            }
            catch (Exception error)
            {
                Log(logPath, error.ToString());
                return 1;
            }
        }

        private static void Log(string path, string message)
        {
            if (!String.IsNullOrEmpty(path))
                File.WriteAllText(path, message + Environment.NewLine, new UTF8Encoding(false));
        }

        private static void Verify(string ns)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    Add(zip, "_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" +
                        "<Relationship Id='R1' Type='" + ns + "/fixedrepresentation' Target='/Documents/job.fdseq'/>" +
                        "<Relationship Id='R2' Type='http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties' Target='/Metadata/core.xml'/>" +
                        "</Relationships>");
                    Add(zip, "Metadata/core.xml", "<cp:coreProperties xmlns:cp='http://schemas.openxmlformats.org/package/2006/metadata/core-properties' " +
                        "xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Prüfung</dc:title></cp:coreProperties>");
                    Add(zip, "Documents/job.fdseq", "<FixedDocumentSequence xmlns='" + ns + "'>" +
                        "<DocumentReference Source='1/FixedDocument.fdoc'/></FixedDocumentSequence>");
                    Add(zip, "Documents/1/FixedDocument.fdoc", "<FixedDocument xmlns='" + ns + "'>" +
                        "<PageContent Source='Pages/1.fpage'/><PageContent Source='Pages/2.fpage'/></FixedDocument>");
                    Add(zip, "Documents/1/Pages/1.fpage", "<FixedPage xmlns='" + ns + "' Width='793' Height='1122'>" +
                        Glyph(28, 78, "Prüfbericht") +
                        "<Canvas RenderTransform='1,0,0,1,30,20'>" +
                        Glyph(14, 100, "Die Straße hinter dem Büro öffnet um neun.") +
                        Glyph(14, 117, "Dieser Absatz bleibt zusammen.") +
                        Glyph(14, 155, "• Apfel") + Glyph(14, 172, "• Birne") +
                        "</Canvas></FixedPage>");
                    Add(zip, "Documents/1/Pages/2.fpage", "<FixedPage xmlns='" + ns + "' Width='793' Height='1122'/>");
                }
                memory.Position = 0;
                var result = XpsMarkdown.Convert(memory, null);
                if (result.Title != "Prüfung" ||
                    !result.Markdown.Contains("# Prüfbericht") ||
                    !result.Markdown.Contains("um neun. Dieser Absatz") ||
                    !result.Markdown.Contains("- Apfel\n- Birne") ||
                    !result.Markdown.Contains("Auf dieser Seite wurde kein eingebetteter Text gefunden."))
                    throw new InvalidDataException("Unexpected Markdown output.");
            }
        }

        private static string Glyph(int size, int y, string text)
        {
            return "<Glyphs FontRenderingEmSize='" + size + "' OriginX='60' OriginY='" + y +
                "' UnicodeString='" + text + "'/>";
        }

        private static void Add(ZipArchive archive, string name, string xml)
        {
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)))
                writer.Write(xml);
        }
    }
}
