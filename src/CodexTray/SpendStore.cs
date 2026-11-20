using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTray.Core;

namespace CodexTray;

internal sealed class SpendStore
{
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTray");
    private string KeyPath => Path.Combine(_directory, "billing-key.dpapi");
    private string ReportPath => Path.Combine(_directory, "monthly-spend.json");

    public bool HasKey => File.Exists(KeyPath);

    public string ReadKey()
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void SetKey(string? key)
    {
        // A replacement key may belong to a different organization.
        if (File.Exists(ReportPath))
        {
            File.Delete(ReportPath);
        }
        if (key is null)
        {
            File.Delete(KeyPath);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(key);
        try { Write(KeyPath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public MonthlySpend? LoadReport()
    {
        try
        {
            var report = JsonSerializer.Deserialize<MonthlySpend>(File.ReadAllBytes(ReportPath));
            return report?.Rows is not null && report.Rows.All(r => !string.IsNullOrWhiteSpace(r?.Name))
                   && report.IsCurrentMonth(DateTimeOffset.UtcNow) ? report : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(MonthlySpend report) => Write(ReportPath, JsonSerializer.SerializeToUtf8Bytes(report));

    private void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(path + ".tmp", data);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
