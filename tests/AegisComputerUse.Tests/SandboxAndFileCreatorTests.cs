using System.IO;
using System.IO.Compression;
using System.Text;
using AegisComputerUse.Core.Files;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Tests;

public sealed class SandboxAndFileCreatorTests : IDisposable
{
    private readonly string _sandboxDir = Path.Combine(Path.GetTempPath(), "AegisSandboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_sandboxDir))
            Directory.Delete(_sandboxDir, recursive: true);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData(@"..\..\Windows\System32\evil.txt")]
    [InlineData(@"sub\..\..\escape.txt")]
    [InlineData(@"C:\temp\abs.txt")]
    [InlineData(@"\\attacker\share\evil.txt")]
    [InlineData("/etc/passwd.txt")]
    [InlineData("notes.txt:hiddenstream")]
    [InlineData("CON.txt")]
    [InlineData("NUL.md")]
    [InlineData("COM1.json")]
    [InlineData("noextension")]
    [InlineData("malware.exe")]
    [InlineData("dropper.ps1")]
    [InlineData("script.bat")]
    [InlineData("hook.dll")]
    [InlineData("macro.docm")]
    [InlineData("shortcut.lnk")]
    [InlineData("payload.vbs")]
    [InlineData("bad*.txt")]
    [InlineData("evil.exe.")]
    [InlineData("space.txt ")]
    public void SafeFileSandbox_BlocksTraversalAdsReservedAndExecutablePaths(string dangerousPath)
    {
        var sandbox = new SafeFileSandbox(_sandboxDir);
        Assert.Throws<SandboxSecurityException>(() => sandbox.ResolveSafePath(dangerousPath));
        Assert.Throws<SandboxSecurityException>(() => sandbox.WriteBytesSafe(dangerousPath, [1, 2, 3]));
    }

    [Fact]
    public void SafeFileSandbox_BlocksProtectedOsRootsAndDriveRoots()
    {
        Assert.Throws<SandboxSecurityException>(() => new SafeFileSandbox(@"C:\"));
        Assert.Throws<SandboxSecurityException>(() => new SafeFileSandbox(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
    }

    [Fact]
    public void SafeFileSandbox_EnforcesSizeAndSessionBudget()
    {
        var sandbox = new SafeFileSandbox(_sandboxDir, maxFileSizeBytes: 64, maxFilesPerSession: 2);
        Assert.Throws<SandboxSecurityException>(() => sandbox.WriteBytesSafe("big.txt", new byte[65]));

        sandbox.WriteBytesSafe("one.txt", [1, 2]);
        sandbox.WriteBytesSafe("two.txt", [3, 4]);
        Assert.Throws<SandboxSecurityException>(() => sandbox.WriteBytesSafe("three.txt", [5]));
    }

    [Fact]
    public void SafeFileSandbox_BlocksNtfsJunctionReparsePoints()
    {
        var sandbox = new SafeFileSandbox(_sandboxDir);
        string outsideDir = Path.Combine(Path.GetTempPath(), "AegisOutsideTarget_" + Guid.NewGuid().ToString("N"));
        string junctionPath = Path.Combine(_sandboxDir, "linked_out");
        Directory.CreateDirectory(outsideDir);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);

            if (Directory.Exists(junctionPath) &&
                (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0)
            {
                Assert.Throws<SandboxSecurityException>(() => sandbox.WriteBytesSafe(@"linked_out\escape.txt", [1, 2, 3]));
            }
        }
        finally
        {
            if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath, recursive: false);
            if (Directory.Exists(outsideDir)) Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void UniversalFileCreator_CreatesValidDocxXlsxPdfPngRtfJsonAndCodeFiles()
    {
        var sandbox = new SafeFileSandbox(_sandboxDir);
        var creator = new UniversalFileCreator(sandbox);

        // 1. DOCX
        var docx = creator.CreateFile("reports/summary.docx", "# Отчёт Aegis\nПроверка кириллицы и English text.");
        Assert.True(File.Exists(docx.FullPath));
        using (var zip = ZipFile.OpenRead(docx.FullPath))
        {
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            var docEntry = zip.GetEntry("word/document.xml");
            Assert.NotNull(docEntry);
            using var sr = new StreamReader(docEntry!.Open(), Encoding.UTF8);
            string xml = sr.ReadToEnd();
            Assert.Contains("Отчёт Aegis", xml, StringComparison.Ordinal);
        }

        // 2. XLSX with formula-injection neutralization
        var xlsx = creator.CreateFile("reports/data.xlsx", "Name,Formula\nAlpha,=CMD|'/C calc'!A0\nBeta,42.5");
        using (var zip = ZipFile.OpenRead(xlsx.FullPath))
        {
            var sheet = zip.GetEntry("xl/worksheets/sheet1.xml");
            Assert.NotNull(sheet);
            using var sr = new StreamReader(sheet!.Open(), Encoding.UTF8);
            string xml = sr.ReadToEnd();
            Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
            Assert.Contains("'=CMD", xml, StringComparison.Ordinal);
            Assert.Contains("42.5", xml, StringComparison.Ordinal);
        }

        // 3. PDF (Russian + English)
        var pdf = creator.CreateFile("reports/brief.pdf", "Тестовый PDF документ\nLine 2 English");
        byte[] pdfBytes = File.ReadAllBytes(pdf.FullPath);
        string header = Encoding.ASCII.GetString(pdfBytes, 0, 8);
        string tail = Encoding.ASCII.GetString(pdfBytes, pdfBytes.Length - 10, 10);
        Assert.StartsWith("%PDF-1.4", header, StringComparison.Ordinal);
        Assert.Contains("%%EOF", tail, StringComparison.Ordinal);

        // 4. PNG
        var png = creator.CreateFile("images/card.png", "Aegis Visual Status OK");
        byte[] pngBytes = File.ReadAllBytes(png.FullPath);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], pngBytes[..4]);

        // 5. RTF & JSON & Code
        var rtf = creator.CreateFile("notes/note.rtf", "Привет мир!");
        Assert.StartsWith(@"{\rtf1", File.ReadAllText(rtf.FullPath), StringComparison.Ordinal);

        var json = creator.CreateFile("data/config.json", "{\"ok\":true,\"items\":[1,2]}");
        Assert.Contains("\"ok\": true", File.ReadAllText(json.FullPath), StringComparison.Ordinal);

        var py = creator.CreateFile("code/hello.py", "print('hello')");
        Assert.Equal("print('hello')", File.ReadAllText(py.FullPath));
    }
}
