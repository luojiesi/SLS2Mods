using Steamworks;

// Uploads (creates or updates) a Slay the Spire 2 Steam Workshop item through the running, logged-in Steam client.
// Usage:
//   WorkshopUploader create|update --content <dir> --title <t> --description <file> [--preview <png>] [--visibility public|private|friends|unlisted] [--item <id>] [--note <text>] [--tags a,b]
const uint AppId = 2868840;

static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

if (args.Length == 0) { Console.Error.WriteLine("usage: create|update --content <dir> --title <t> --description <file> [--preview <png>] [--visibility ...] [--item <id>] [--note <text>] [--tags a,b]"); return 2; }
string mode = args[0];
if (mode == "check")
{
    if (!SteamAPI.Init()) { Console.Error.WriteLine("SteamAPI.Init failed: is Steam running and logged in?"); return 1; }
    Console.WriteLine($"Steam OK: user {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()}), app {SteamUtils.GetAppID()}");
    SteamAPI.Shutdown();
    return 0;
}
string content = Path.GetFullPath(Arg(args, "--content") ?? throw new ArgumentException("--content required"));
string title = Arg(args, "--title") ?? throw new ArgumentException("--title required");
string description = File.ReadAllText(Arg(args, "--description") ?? throw new ArgumentException("--description required"));
string? preview = Arg(args, "--preview") is { } p ? Path.GetFullPath(p) : null;
string visibilityArg = Arg(args, "--visibility") ?? "public";
string note = Arg(args, "--note") ?? "";
string[] tags = (Arg(args, "--tags") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
ulong itemId = ulong.TryParse(Arg(args, "--item"), out var id) ? id : 0;
if (mode == "update" && itemId == 0) { Console.Error.WriteLine("update needs --item <id>"); return 2; }
if (!Directory.Exists(content)) { Console.Error.WriteLine($"content dir not found: {content}"); return 2; }

var visibility = visibilityArg switch
{
    "public" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic,
    "friends" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly,
    "unlisted" => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted,
    _ => ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate,
};

if (!SteamAPI.Init()) { Console.Error.WriteLine("SteamAPI.Init failed: is Steam running and logged in?"); return 1; }
try
{
    Console.WriteLine($"Steam user: {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()})");
    var appId = new AppId_t(AppId);

    if (mode == "create")
    {
        bool done = false, ok = false, legal = false;
        var cr = CallResult<CreateItemResult_t>.Create((r, failed) =>
        {
            ok = !failed && r.m_eResult == EResult.k_EResultOK; legal = r.m_bUserNeedsToAcceptWorkshopLegalAgreement;
            itemId = r.m_nPublishedFileId.m_PublishedFileId; done = true;
            Console.WriteLine($"CreateItem: {r.m_eResult} id={itemId} needsLegalAgreement={legal}");
        });
        cr.Set(SteamUGC.CreateItem(appId, EWorkshopFileType.k_EWorkshopFileTypeCommunity));
        while (!done) { SteamAPI.RunCallbacks(); Thread.Sleep(50); }
        if (!ok) return 1;
        if (legal) Console.WriteLine($"NOTE: accept the Workshop legal agreement at https://steamcommunity.com/sharedfiles/workshoplegalagreement before the item becomes visible.");
    }

    var handle = SteamUGC.StartItemUpdate(appId, new PublishedFileId_t(itemId));
    SteamUGC.SetItemTitle(handle, title);
    SteamUGC.SetItemDescription(handle, description);
    SteamUGC.SetItemContent(handle, content);
    if (preview != null) SteamUGC.SetItemPreview(handle, preview);
    SteamUGC.SetItemVisibility(handle, visibility);
    if (tags.Length > 0) SteamUGC.SetItemTags(handle, tags.ToList());

    bool sdone = false, sok = false;
    var sr = CallResult<SubmitItemUpdateResult_t>.Create((r, failed) =>
    {
        sok = !failed && r.m_eResult == EResult.k_EResultOK; sdone = true;
        Console.WriteLine($"SubmitItemUpdate: {r.m_eResult} needsLegalAgreement={r.m_bUserNeedsToAcceptWorkshopLegalAgreement}");
    });
    sr.Set(SteamUGC.SubmitItemUpdate(handle, note));
    var last = EItemUpdateStatus.k_EItemUpdateStatusInvalid;
    while (!sdone)
    {
        SteamAPI.RunCallbacks();
        var st = SteamUGC.GetItemUpdateProgress(handle, out ulong processed, out ulong total);
        if (st != last) { Console.WriteLine($"  {st} {processed}/{total}"); last = st; }
        Thread.Sleep(100);
    }
    if (!sok) return 1;
    Console.WriteLine($"DONE: https://steamcommunity.com/sharedfiles/filedetails/?id={itemId}");
    return 0;
}
finally { SteamAPI.Shutdown(); }
