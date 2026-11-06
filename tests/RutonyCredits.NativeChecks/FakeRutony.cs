namespace RutonyChat;
public static class ProgramProps { public enum SiteEnum { other, twitch, vkplay } }
public static class CurrencyControl
{
    public sealed class CurrencyItem { public string CharCode; }
    public sealed class SumClass(float sum, CurrencyItem currency) { public readonly float sum=sum; public readonly CurrencyItem currency=currency; }
}
public static class RutonyBot { public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages=new(); public static void SayToWindow(string text) => Messages.Enqueue(text); }
public static class ChatEventServer
{
    public delegate void ChatMessageHandler(ProgramProps.SiteEnum site,string user,string text,Dictionary<string,object> parameters);
    public static event ChatMessageHandler OnChatMessage;
    public static void Message(ProgramProps.SiteEnum site,string name,bool? moderator,string id=null)
    {
        var parameters=new Dictionary<string,object>();
        if(moderator.HasValue) parameters["moderator"]=moderator.Value;
        if(id!=null) parameters["user_id"]=id;
        OnChatMessage?.Invoke(site,name,"PRIVATE_TEST_CHAT_TEXT_MUST_NOT_BE_STORED",parameters);
    }
    public static event Action<ProgramProps.SiteEnum,string> OnFollower, OnAnySubscriber;
    public static event Action<ProgramProps.SiteEnum,string,int> OnRaid;
    public static event Action<ProgramProps.SiteEnum,string,CurrencyControl.SumClass,string,string> OnDonate;
    public static void Follower(ProgramProps.SiteEnum s,string n) => OnFollower?.Invoke(s,n);
    public static void Subscriber(ProgramProps.SiteEnum s,string n) => OnAnySubscriber?.Invoke(s,n);
    public static void Raid(ProgramProps.SiteEnum s,string n,int q) => OnRaid?.Invoke(s,n,q);
    public static void Donate(string name,float? sum,string currency) =>
        OnDonate?.Invoke(ProgramProps.SiteEnum.other,name,sum.HasValue ? new CurrencyControl.SumClass(sum.Value,new CurrencyControl.CurrencyItem{CharCode=currency}) : null,"unused message","unused message");
}

public static class RutonyBotFunctions
{
    public static string RegisteredDirectory;
    public static bool ThrowOnLookup;
    public static readonly List<string> Lookups = new();
    public static string GetScriptDirectory(string scriptname)
    {
        Lookups.Add(scriptname);
        if (ThrowOnLookup) throw new InvalidOperationException("Synthetic SDK lookup failure");
        return RegisteredDirectory;
    }
}
