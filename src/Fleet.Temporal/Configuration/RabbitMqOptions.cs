namespace Fleet.Temporal.Configuration;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";
    public string Host { get; set; } = "";
    public string Exchange { get; set; } = "fleet.group";
}
