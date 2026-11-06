namespace RutonyCredits;

public sealed class StoreOverloadedException : InvalidOperationException
{
    public StoreOverloadedException() : base("Очередь команд заполнена. Дождитесь обработки и повторите запрос.") { }
}
