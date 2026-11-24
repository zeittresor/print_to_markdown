using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Print to Markdown")]
[assembly: AssemblyVersion("0.2.4.0")]

namespace PrintToMarkdown
{
    internal sealed class Receiver : ApplicationContext
    {
        private const string AppName = "Print to Markdown";
        private const int Port = 19101;
        private readonly BlockingCollection<object> results = new BlockingCollection<object>(4);
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
        private readonly NotifyIcon tray;
        private TcpListener listener;
        private string lastFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        [STAThread]
        private static int Main(string[] arguments)
        {
            if (arguments.Length != 0 && arguments[0] == "--self-test")
                return SelfTest.Run(arguments.Length > 1 ? arguments[1] : null);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var receiver = new Receiver())
                    Application.Run(receiver);
                return 0;
            }
            catch (SocketException error)
            {
                MessageBox.Show("Druckempfang nicht verfügbar. Prüfe, ob die App bereits läuft.\r\n" +
                    error.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private Receiver()
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.ExclusiveAddressUse = true;
            listener.Start(4);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Beenden", null, (sender, args) => ExitThread());
            tray = new NotifyIcon
            {
                Text = "Print to Markdown (.md)",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
            };
            var thread = new Thread(Receive) { IsBackground = true, Name = "XPS print receiver" };
            thread.Start();
            timer.Tick += HandleResult;
            timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (listener != null)
                    listener.Stop();
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.ContextMenuStrip.Dispose();
                    tray.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void Receive()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    using (var spool = new MemoryStream())
                    {
                        using (client)
                        {
                            client.ReceiveTimeout = 90000;
                            CopyJob(client.GetStream(), spool);
                        }
                        if (spool.Length > 0)
                        {
                            spool.Position = 0;
                            results.Add(XpsMarkdown.Convert(spool, null));
                        }
                    }
                }
                catch (Exception ex)
                {
                    results.Add(ex);
                }
            }
        }

        private static void CopyJob(Stream source, Stream destination)
        {
            byte[] buffer = new byte[65536];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (destination.Length + count > XpsMarkdown.JobLimit)
                    throw new InvalidDataException("Der Druckauftrag überschreitet 128 MiB.");
                destination.Write(buffer, 0, count);
            }
        }

        private void HandleResult(object sender, EventArgs args)
        {
            object result;
            if (!results.TryTake(out result))
                return;
            timer.Stop();
            try
            {
                var error = result as Exception;
                if (error != null)
                {
                    MessageBox.Show(error.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Save((XpsMarkdown.Document)result);
                }
            }
            finally
            {
                timer.Start();
            }
        }

        private void Save(XpsMarkdown.Document document)
        {
            using (var picker = new SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md",
                Title = "Druckauftrag als Markdown speichern",
                DefaultExt = "md",
                AddExtension = true,
                RestoreDirectory = true,
                InitialDirectory = lastFolder,
                FileName = SuggestedName(document.Title)
            })
            {
                while (picker.ShowDialog() == DialogResult.OK)
                {
                    if (!String.Equals(Path.GetExtension(picker.FileName), ".md", StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("Wähle einen Dateinamen mit der Endung .md.", AppName);
                        continue;
                    }
                    try
                    {
                        SaveFile(picker.FileName, document.Markdown);
                        lastFolder = Path.GetDirectoryName(picker.FileName);
                        return;
                    }
                    catch (IOException ex)
                    {
                        MessageBox.Show(ex.Message, "Speichern fehlgeschlagen");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        MessageBox.Show(ex.Message, "Speichern fehlgeschlagen");
                    }
                }
            }
        }

        private static string SuggestedName(string title)
        {
            string name = String.IsNullOrWhiteSpace(title) ? "Druckauftrag" : title.Trim();
            name = String.Concat(name.Take(120).Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim(' ', '.');
            foreach (string extension in new[] { ".xps", ".oxps", ".doc", ".docx", ".pdf", ".txt", ".md" })
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - extension.Length).TrimEnd(' ', '.');
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                name = "_" + name;
            return (name.Length == 0 ? "Druckauftrag" : name) + ".md";
        }

        private static void SaveFile(string target, string markdown)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".ptm-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var text = new StreamWriter(file, new UTF8Encoding(false)))
                    text.Write(markdown);
                if (File.Exists(target))
                    File.Replace(temporary, target, null);
                else
                    File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }
}
