using System.Diagnostics;

namespace CodexTray;

internal static class Brand
{
    public const string CodexUsageUrl = "https://chatgpt.com/codex/cloud/settings/analytics#usage";
    public const string AdminKeysUrl = "https://platform.openai.com/settings/organization/admin-keys";
    public const string SpendUrl = "https://platform.openai.com/analytics/spend";
    public const string BlogUrl = "https://www.vcloudinfo.com";

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Codex Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static Icon LoadApplicationIcon() =>
        Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        ?? (Icon)SystemIcons.Application.Clone();
}
