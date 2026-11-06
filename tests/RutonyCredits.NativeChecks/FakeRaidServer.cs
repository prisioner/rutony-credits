namespace RutonyChat
{
    public static class ChatLocalServer
    {
        public static bool httpServerActive;
        public static object _HttpServer;
    }
    public sealed class FakeRutonyHttpServer
    {
        public int Port { get; set; }
        public bool IsSecure { get; set; }
        public bool IsListening { get; set; } = true;
    }
}