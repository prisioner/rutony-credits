namespace RutonyChat.ChatServices
{
    public class ChatService
    {
        public enum StateEnum { Stopped, Progress, OK }
        public StateEnum State = StateEnum.OK;
        public string PathValue;
        public int PathReads;
        public virtual string GetChannelPath() { Interlocked.Increment(ref PathReads); return PathValue; }
    }
 }
namespace RutonyChat
{
    using RutonyChat.ChatServices;
    public sealed class FakeVkService : ChatService { public global::OAuth.VKPlayAPI ServiceAPI; }
    public static class ChatServer
    {
        public static readonly Dictionary<ProgramProps.SiteEnum,ChatService> Services = new();
        public static ChatService GetParent(ProgramProps.SiteEnum site) => GetChatServer(site);
        public static ChatService GetChatServer(ProgramProps.SiteEnum site) => Services.GetValueOrDefault(site);
    }
}
namespace OAuth
{
    public class OAuthBase
    {
        public enum StatesAuth { idle, updating, ready, error, disposing }
        public StatesAuth State {get;set;} = StatesAuth.ready;
    }
    public sealed class VKPlayAPI : OAuthBase
    {
        public sealed class OwnerItem { public int id; public string nick; }
        public sealed class DataItem { public OwnerItem owner; }
        public sealed class ChannelInfoItem { public DataItem data; }
        public ChannelInfoItem Result;
        public TaskCompletionSource<ChannelInfoItem> Gate;
        public int Requests;
        public async Task<ChannelInfoItem> GetChannelInfoAsync(CancellationToken token=default)
        {
            Interlocked.Increment(ref Requests);
            return Gate==null ? Result : await Gate.Task;
        }
    }
}
