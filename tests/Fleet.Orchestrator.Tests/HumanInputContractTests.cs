using System.Text.Json;
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests;

public class HumanInputContractTests
{
    [Theory]
    [InlineData("text", "  literal\n{{config.Secret}} ")]
    [InlineData("choice", "first")]
    [InlineData("approve-reject", "approved")]
    public void RequestAndAnswer_ValidModes_PreserveLiteralData(string mode, string value)
    {
        var request = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        { ["Question"] = "\"Кыргыз\"\n{{input.Secret}}", ["Context"] = " \\ context ", ["AnswerMode"] = mode });
        if (mode == "choice") request = JsonSerializer.SerializeToElement(new { Question = "q", Context = "c", AnswerMode = mode,
            Choices = new[] { new { Id = "first", Label = "First" }, new { Id = "other", Label = "Other" } } });
        HumanInputContract.ValidateRequest(request);
        var answer = HumanInputContract.ValidateAnswer(request, JsonSerializer.SerializeToElement(new { Value = value, Comment = " \ncomment " }));
        Assert.Equal(value, answer.GetProperty("Value").GetString());
        Assert.Equal(" \ncomment ", answer.GetProperty("Comment").GetString());
    }

    [Theory]
    [InlineData("{\"Question\":\"q\",\"Context\":\"c\",\"AnswerMode\":\"unknown\"}")]
    [InlineData("{\"Question\":\"q\",\"Question\":\"q\",\"Context\":\"c\",\"AnswerMode\":\"text\"}")]
    [InlineData("{\"Question\":\"q\",\"Context\":\"c\",\"AnswerMode\":\"choice\",\"Choices\":[{\"Id\":\"x\",\"Label\":\"X\"},{\"Id\":\"x\",\"Label\":\"Y\"}]}")]
    [InlineData("{\"Question\":\"q\",\"Context\":\"c\",\"AnswerMode\":\"text\",\"Choices\":[]}")]
    public void Request_InvalidShape_Refused(string json) => Assert.Throws<ArgumentException>(() => HumanInputContract.ValidateRequest(JsonSerializer.Deserialize<JsonElement>(json)));

    [Theory]
    [InlineData("{\"Value\":\"approved\",\"Ref\":\"forged\"}")]
    [InlineData("{\"Value\":\"approved\",\"Value\":\"rejected\"}")]
    [InlineData("{\"value\":\"approved\"}")]
    [InlineData("{\"Value\":\"not-declared\"}")]
    public void Answer_InvalidShapeOrMembership_Refused(string json) => Assert.Throws<ArgumentException>(() => HumanInputContract.ValidateAnswer(
        JsonSerializer.SerializeToElement(new { Question = "q", Context = "c", AnswerMode = "approve-reject" }), JsonSerializer.Deserialize<JsonElement>(json)));
}
