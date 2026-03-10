namespace MonocoBot.Configuration;

public class BotOptions
{
    public string Name { get; set; } = "Monoco";
    public string DiscordToken { get; set; } = "";
    public string AiProvider { get; set; } = "openai";
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string AiApiKey { get; set; } = "";
    public string AiEndpoint { get; set; } = "";
    public int HealthPort { get; set; } = 8080;
    public int MaxConversationHistory { get; set; } = 50;
}
