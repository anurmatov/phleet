using System.Xml.Linq;
using Fleet.Contracts;

namespace Fleet.Protocol.Tests;

/// <summary>
/// <c>Fleet.Contracts</c> holds what one fleet service sends another, and every service references
/// it. Whatever it depends on ships in all of them, so it depends on nothing but the framework.
/// </summary>
public sealed class ContractsProjectDependencyTests
{
    private static DirectoryInfo RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
                directory = directory.Parent;

            Assert.True(directory is not null, "could not locate the repository root");
            return directory!;
        }
    }

    [Fact]
    public void The_project_declares_no_package_and_no_project_reference()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot.FullName, "src", "Fleet.Contracts", "Fleet.Contracts.csproj"));

        var references = project.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference" or "Reference" or "FrameworkReference")
            .Select(e => $"{e.Name.LocalName} {e.Attribute("Include")?.Value}")
            .ToArray();

        Assert.True(
            references.Length == 0,
            $"Fleet.Contracts declares {string.Join(", ", references)}. Every service references this "
            + "project, so a dependency added here ships in all of them.");
    }

    /// <summary>
    /// From the compiled assembly, because a reference can arrive through a shared build file
    /// without appearing in the project.
    /// </summary>
    [Fact]
    public void The_assembly_references_the_framework_and_nothing_else()
    {
        var outside = typeof(RelayMessage).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? "")
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                && name is not ("System" or "netstandard" or "mscorlib"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(outside);
    }

    /// <summary>
    /// Each producer writes the fields it has always written, in the declared order. The samples
    /// under <c>tests/fixtures/wire</c> hold each service to its bytes; this holds the rule itself.
    /// </summary>
    [Theory]
    [InlineData(RelayShape.Envelope, "ChatId,Sender,Text,Timestamp,Type")]
    [InlineData(RelayShape.Bridge, "ChatId,Sender,Text,Timestamp,Type,CorrelationId,TaskId")]
    [InlineData(RelayShape.Agent, "ChatId,Sender,Text,Timestamp,Type,CorrelationId,TaskId,WorkflowId,SignalName")]
    [InlineData(RelayShape.Delegation, "ChatId,Sender,Text,Timestamp,Type,CorrelationId,TaskId,WorkflowId,SignalName,Repo")]
    public void A_relay_message_is_written_with_its_producers_fields_and_read_back_whole(RelayShape shape, string fields)
    {
        var message = new RelayMessage(
            7, "sender", "text", DateTimeOffset.UnixEpoch, RelayMessageType.Response,
            CorrelationId: "c", TaskId: "t", WorkflowId: "w", SignalName: "s", Repo: "org/app");

        var json = System.Text.Encoding.UTF8.GetString(RelayWire.Serialize(message, shape));

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(fields.Split(','), document.RootElement.EnumerateObject().Select(p => p.Name));

        // A consumer reads any producer's message: what was not written is the default.
        var read = RelayWire.Deserialize(json)!;
        Assert.Equal(message.ChatId, read.ChatId);
        Assert.Equal(message.Sender, read.Sender);
        Assert.Equal(message.Text, read.Text);
        Assert.Equal(message.Timestamp, read.Timestamp);
        Assert.Equal(message.Type, read.Type);
        Assert.Equal(shape >= RelayShape.Bridge ? "c" : null, read.CorrelationId);
        Assert.Equal(shape >= RelayShape.Bridge ? "t" : null, read.TaskId);
        Assert.Equal(shape >= RelayShape.Agent ? "w" : null, read.WorkflowId);
        Assert.Equal(shape >= RelayShape.Agent ? "s" : null, read.SignalName);
        Assert.Equal(shape >= RelayShape.Delegation ? "org/app" : null, read.Repo);
    }

    /// <summary>
    /// The execution id is the one field written only when set, and only by the two producers that
    /// have one: an agent answering a durable delegation, and the delegation itself.
    /// </summary>
    [Theory]
    [InlineData(RelayShape.Envelope, false)]
    [InlineData(RelayShape.Bridge, false)]
    [InlineData(RelayShape.Agent, true)]
    [InlineData(RelayShape.Delegation, true)]
    public void An_execution_id_is_written_last_by_the_producers_that_have_one_and_never_when_unset(
        RelayShape shape, bool written)
    {
        var unset = new RelayMessage(7, "sender", "text", DateTimeOffset.UnixEpoch, RelayMessageType.Response);
        var set = unset with { ExecutionId = "execution-1" };

        var withoutId = System.Text.Encoding.UTF8.GetString(RelayWire.Serialize(unset, shape));
        var withId = System.Text.Encoding.UTF8.GetString(RelayWire.Serialize(set, shape));

        Assert.DoesNotContain("ExecutionId", withoutId);
        if (!written)
        {
            Assert.Equal(withoutId, withId);
            return;
        }

        // The same bytes, and the id after them.
        Assert.Equal(withoutId[..^1] + ",\"ExecutionId\":\"execution-1\"}", withId);
        Assert.Equal("execution-1", RelayWire.Deserialize(withId)!.ExecutionId);
    }

    [Fact]
    public void A_relay_message_without_a_type_is_a_directive()
    {
        var read = RelayWire.Deserialize(
            """{"ChatId":1,"Sender":"a","Text":"t","Timestamp":"2026-01-02T03:04:05+00:00"}""")!;

        Assert.Equal(RelayMessageType.Directive, read.Type);
    }
}
