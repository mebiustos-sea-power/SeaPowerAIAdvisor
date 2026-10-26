using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SeaPowerAIAdvisor;

internal static class UpdateNoticeTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("A release without an update notice stays silent while malformed notice definitions fail visibly", () => InTemp(directory =>
        {
            var store = new AdvisorUpdateNoticeStore(directory);
            Require(store.Unread(AdvisorUpdateNotice.Parse("null")) == null && !Directory.EnumerateFileSystemEntries(directory).Any(),
                "A release without a notice created read state");
            foreach (var invalid in new[] { "{}", "[]", "null null", "{\"id\":\"\",\"title_key\":\"title\",\"body_key\":\"body\"}",
                "{\"id\":\"a\",\"id\":\"b\",\"title_key\":\"title\",\"body_key\":\"body\"}" })
            {
                var rejected = false;
                try { _ = AdvisorUpdateNotice.Parse(invalid); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "A broken notice definition was treated as no notice");
            }
            var bundled = AdvisorUpdateNotice.Current;
            if (bundled != null)
                foreach (var language in new[] { "en", "ja", "fr" })
                {
                    var text = AdvisorText.Load(language);
                    Require(!string.IsNullOrWhiteSpace(text[bundled.TitleKey]) && !string.IsNullOrWhiteSpace(text[bundled.BodyKey]),
                        "The installed notice cannot be displayed in a supported or fallback language");
                }
        }));
        check("Showing a notice never acknowledges it, and dismissal survives reopened windows and separate mission stores", () => InTemp(directory =>
        {
            var notice = Notice("current"); var store = new AdvisorUpdateNoticeStore(directory);
            var path = Path.Combine(directory, "read-notices.json");
            Require(store.Unread(notice) == notice && store.Unread(notice) == notice && !File.Exists(path),
                "Merely finding an unread notice acknowledged it");
            File.WriteAllText(Path.Combine(directory, "options.json"), "unrelated settings");
            store.MarkRead(notice); var before = File.ReadAllBytes(path);
            Require(store.Unread(notice) == null && new AdvisorUpdateNoticeStore(directory).Unread(notice) == null,
                "The notice reappeared after reopening or switching mission stores");
            new AdvisorUpdateNoticeStore(directory).MarkRead(notice);
            Require(File.ReadAllBytes(path).SequenceEqual(before) && File.ReadAllText(Path.Combine(directory, "options.json")) == "unrelated settings" &&
                !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Dismissal rewrote settings, changed an existing receipt or left a temporary file");
        }));
        check("Notice previews bypass read receipts without creating, rewriting or repairing user state", () => InTemp(directory =>
        {
            var notice = Notice("preview"); var store = new AdvisorUpdateNoticeStore(directory); var path = Path.Combine(directory, "read-notices.json");
            Require(store.Unread(notice, preview: true) == notice, "A fresh notice could not be previewed");
            store.MarkRead(notice, preview: true);
            Require(!File.Exists(path) && store.Unread(notice) == notice, "Preview dismissal acknowledged an unread notice");
            store.MarkRead(notice); var read = File.ReadAllBytes(path);
            Require(store.Unread(notice) == null && store.Unread(notice, preview: true) == notice, "Read notices were not previewable");
            store.MarkRead(notice, preview: true);
            Require(File.ReadAllBytes(path).SequenceEqual(read), "Preview rewrote read receipts");
            File.WriteAllText(path, "{broken"); var broken = File.ReadAllBytes(path);
            Require(store.Unread(notice, preview: true) == notice && store.Unread(null, preview: true) == null, "Preview consulted receipt state");
            store.MarkRead(notice, preview: true);
            Require(File.ReadAllBytes(path).SequenceEqual(broken), "Preview replaced corrupt receipt state");
            Expect("notice_state_invalid", () => store.Unread(notice));
        }));
        check("Only the installed notice is offered after skipped releases, with receipts independent of language and retaining earlier acknowledgements", () => InTemp(directory =>
        {
            var old = Notice("old"); var latest = Notice("latest");
            var battle = new AdvisorUpdateNoticeStore(directory); var campaign = new AdvisorUpdateNoticeStore(directory);
            battle.MarkRead(old);
            Require(campaign.Unread(latest) == latest, "A receipt for an old release suppressed the new notice");
            var renamed = AdvisorUpdateNotice.Parse("{\"id\":\"latest\",\"title_key\":\"translated_title\",\"body_key\":\"translated_body\"}")!;
            campaign.MarkRead(latest);
            Require(battle.Unread(renamed) == null && battle.Unread(old) == null,
                "Changing language or returning to an older notice ignored an existing receipt");
            var before = File.ReadAllBytes(Path.Combine(directory, "read-notices.json"));
            Require(battle.Unread(null) == null && File.ReadAllBytes(Path.Combine(directory, "read-notices.json")).SequenceEqual(before),
                "Installing a release with no notice changed receipts");
        }));
        check("Unreadable, corrupt and unwritable receipts report failures without replacing data or claiming acknowledgement", () => InTemp(directory =>
        {
            var path = Path.Combine(directory, "read-notices.json"); var notice = Notice("current");
            var store = new AdvisorUpdateNoticeStore(directory);
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes("{broken"), Encoding.UTF8.GetBytes("{\"read_notice_ids\":[17]}"), new byte[] { 0xff } })
            {
                File.WriteAllBytes(path, bytes);
                Expect("notice_state_invalid", () => store.Unread(notice));
                Expect("notice_state_invalid", () => store.MarkRead(notice));
                Require(store.Unread(null) == null && File.ReadAllBytes(path).SequenceEqual(bytes), "Bad receipts were silently reset or written over");
            }
            File.Delete(path); Directory.CreateDirectory(path);
            Expect("notice_state_unreadable", () => store.Unread(notice));
            Expect("notice_state_unreadable", () => store.MarkRead(notice));
            Directory.Delete(path);
            var blockedParent = Path.Combine(directory, "blocked"); File.WriteAllText(blockedParent, "keep this");
            var blocked = new AdvisorUpdateNoticeStore(blockedParent);
            Expect("notice_state_write_failed", () => blocked.MarkRead(notice));
            Require(File.ReadAllText(blockedParent) == "keep this" && blocked.Unread(notice) == notice,
                "A failed save acknowledged the notice or overwrote the blocking file");
        }));
    }
    private static AdvisorUpdateNotice Notice(string id) => AdvisorUpdateNotice.Parse("{\"id\":\"" + id + "\",\"title_key\":\"title\",\"body_key\":\"body\"}")!;
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (AdvisorUpdateNoticeException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected notice state error: " + code);
    }
    private static void InTemp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor-notices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, true); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
