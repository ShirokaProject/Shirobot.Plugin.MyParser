using System.Reflection;
using MyParser.Provider.BiliBili.Views;
using ShiroBot.SDK.Models;

var managerType = typeof(BiliCard).Assembly.GetType("Shirobot.Plugin.MyParser.ParseTaskManager", true)!;
var manager = Activator.CreateInstance(managerType, nonPublic: true)!;
var enter = managerType.GetMethod("TryEnterProviderCooldown")!;
const string video = "https://www.bilibili.com/video/BV1rxp86jEYH/";
var groupA = new Channel("100", ChannelType.Group);
var groupB = new Channel("200", ChannelType.Group);

(bool Blocked, string Identity, TimeSpan Remaining) Try(string platform, Channel channel, string url = video, string provider = "bilibili")
{
    object?[] arguments = [platform, channel, provider, url, null, null];
    var blocked = (bool)enter.Invoke(manager, arguments)!;
    return (blocked, (string)arguments[4]!, (TimeSpan)arguments[5]!);
}

Check(!Try("qq", groupA).Blocked, "First request in group A must pass.");
var duplicate = Try("qq", groupA, video + "?share_source=copy_link");
Check(duplicate.Blocked && duplicate.Remaining > TimeSpan.Zero, "The same work in the same group must remain on cooldown.");
Check(duplicate.Identity == video.TrimEnd('/'), "Different share links must normalize to the same work.");
Check(!Try("qq", groupA, video + "?p=2").Blocked, "Selecting another part must pass immediately.");
Check(Try("qq", groupA, video + "?p=2&share_source=copy").Blocked, "Duplicate requests for the same part must remain on cooldown.");
Check(!Try("qq", groupB).Blocked, "Group A must not put group B on cooldown.");
Check(Try("qq", groupB).Blocked, "Group B must have its own cooldown.");
Check(!Try("telegram", groupA).Blocked, "Matching group IDs on different platforms must remain independent.");
Check(!Try("qq", new Channel("100", ChannelType.Direct)).Blocked, "Direct messages must not share group cooldowns.");
Check(!Try("qq", groupA, provider: "other").Blocked, "Different providers must remain independent.");
Check(!Try("qq", groupA, "https://www.bilibili.com/video/BV1JLpY6DEWU").Blocked, "Different works in the same group must pass.");

var accepted = 0;
Parallel.For(0, 32, _ =>
{
    if (!Try("qq", new Channel("concurrent", ChannelType.Group)).Blocked) Interlocked.Increment(ref accepted);
});
Check(accepted == 1, "Concurrent duplicates in one group must accept exactly one request.");
Console.WriteLine("Same-group cooldown, cross-group/platform/channel/provider isolation and concurrent duplicate checks passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
