using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Protocol;

namespace Fleet.Protocol.Tests;

/// <summary>
/// AC1–AC6: the contracts project's dependency surface, round-tripping, the golden wire shape,
/// forward compatibility, and the absence of producerless kinds.
/// </summary>
public class ProtocolContractTests
{
    private static readonly ConversationIdentity SampleIdentity = new()
    {
        PrincipalId = "p_0000000000000001",
        Role = PrincipalRole.Owner,
        ChannelId = "example-adapter",
        ConversationId = "c_0000000000000001",
        SubmissionId = "s_0000000000000001",
        TurnId = "t_0000000000000001",
        Attempt = 1,
    };

    /// <summary>
    /// AC1. A client must be able to reference the contracts without inheriting Telegram.Bot,
    /// RabbitMQ.Client or Microsoft.Extensions.*. Adding any of them fails here.
    /// </summary>
    [Fact]
    public void Assembly_ReferencesNothingOutsideTheBcl()
    {
        var referenced = typeof(ConversationEvent).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        var forbidden = referenced
            .Where(name =>
                name.StartsWith("Telegram", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("RabbitMQ", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.Extensions", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Fleet.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(forbidden);
    }

    /// <summary>AC2: every v1 kind has a sealed payload record that round-trips.</summary>
    public static TheoryData<string, object?> EveryOutboundKind() => new()
    {
        { ConversationEventKind.ProtocolRejected, new ProtocolRejectedPayload { Code = ProtocolErrorCode.Unauthorized, Message = ProtocolErrors.Unauthorized } },
        { ConversationEventKind.SubmissionText, new SubmissionTextPayload { Text = "what is open?" } },
        { ConversationEventKind.SubmissionAccepted, new SubmissionAcceptedPayload { Disposition = SubmissionDisposition.QueueFull, QueuePosition = 3 } },
        { ConversationEventKind.TurnStarted, new TurnStartedPayload() },
        { ConversationEventKind.TurnProgress, new TurnProgressPayload { Activity = ProgressActivity.Tool, ToolName = "Read" } },
        { ConversationEventKind.TurnNotice, new TurnNoticePayload { Text = "notice" } },
        { ConversationEventKind.TurnRecoveredAnswer, new TurnRecoveredAnswerPayload { Text = "recovered" } },
        { ConversationEventKind.TurnFinal, new TurnFinalPayload { Text = "answer", Completion = TurnCompletion.Completed, IsPartial = false, Truncated = false, MergedSubmissionIds = ["s_0000000000000001"] } },
        { ConversationEventKind.TurnError, new TurnErrorPayload { Code = ProtocolErrorCode.ExecutorError, Message = ProtocolErrors.ExecutorError } },
        { ConversationEventKind.TurnCanceled, new TurnCanceledPayload { Reason = TurnCancelReason.User, MergedSubmissionIds = [] } },
        { ConversationEventKind.TurnOutcomeUnknown, new TurnOutcomeUnknownPayload { Reason = OutcomeUnknownReason.TurnReaped } },
        { ConversationEventKind.ControlAck, new ControlAckPayload { Target = ControlTarget.Cancel, Accepted = true, HadRunningTask = false } },
    };

    [Theory]
    [MemberData(nameof(EveryOutboundKind))]
    public void EveryKind_RoundTrips(string kind, object? payload)
    {
        var evt = ConversationEvent.Create(kind, SampleIdentity, "e_1", 1, DateTimeOffset.UnixEpoch, (object?)payload);

        var json = FleetProtocolJson.Serialize(evt);
        var back = FleetProtocolJson.Deserialize<ConversationEvent>(json);

        Assert.NotNull(back);
        Assert.Equal(kind, back!.Kind);
        Assert.Equal(ProtocolVersion.V1, back.Protocol);
        Assert.Equal(SampleIdentity, back.Identity);
    }

    /// <summary>
    /// AC3. Pins the wire shape. Changing a property name, the camelCase policy, the null-omission
    /// rule or an enum's lowercase rendering fails this test — which is the point: those are
    /// breaking changes that require a major bump, not an accident.
    /// </summary>
    [Fact]
    public void GoldenJson_PinsTheWireShape()
    {
        var evt = ConversationEvent.Create(
            ConversationEventKind.TurnProgress, SampleIdentity, "01JEXAMPLE0000000000000001", 4,
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            new TurnProgressPayload { Activity = ProgressActivity.Tool, ToolName = "Read" });

        var json = FleetProtocolJson.Serialize(evt);

        const string expected =
            """{"protocol":"fleet.conversation.v1","eventId":"01JEXAMPLE0000000000000001","seq":4,"emittedAt":"2026-01-01T12:00:00+00:00","kind":"turn.progress","identity":{"principalId":"p_0000000000000001","role":"owner","channelId":"example-adapter","conversationId":"c_0000000000000001","submissionId":"s_0000000000000001","turnId":"t_0000000000000001","attempt":1},"payload":{"activity":"tool","toolName":"Read"}}""";

        Assert.Equal(expected, json);
    }

    /// <summary>The snake_case-lower enum policy is what makes multi-word values wire-correct.</summary>
    [Theory]
    [InlineData(SubmissionDisposition.QueueFull, "queue_full")]
    [InlineData(SubmissionDisposition.Ran, "ran")]
    public void MultiWordEnums_SerializeAsSnakeCaseLower(SubmissionDisposition disposition, string expected)
    {
        var json = FleetProtocolJson.Serialize(new SubmissionAcceptedPayload { Disposition = disposition });
        Assert.Contains($"\"disposition\":\"{expected}\"", json);
    }

    [Fact]
    public void OutcomeUnknownReason_SerializesAsSnakeCaseLower()
    {
        var json = FleetProtocolJson.Serialize(new TurnOutcomeUnknownPayload { Reason = OutcomeUnknownReason.TurnReaped });
        Assert.Contains("\"reason\":\"turn_reaped\"", json);
    }

    /// <summary>AC4: an unknown kind must be ignorable, never fatal.</summary>
    [Fact]
    public void UnknownKind_DeserializesWithoutThrowing()
    {
        const string json =
            """{"protocol":"fleet.conversation.v1","eventId":"e","seq":1,"emittedAt":"2026-01-01T00:00:00+00:00","kind":"turn.some_future_kind","identity":{"principalId":"p","role":"owner","channelId":"c","conversationId":"cv","submissionId":"s","attempt":1},"payload":{"whatever":true}}""";

        var evt = FleetProtocolJson.Deserialize<ConversationEvent>(json);

        Assert.NotNull(evt);
        Assert.Equal("turn.some_future_kind", evt!.Kind);
        Assert.False(ConversationEventKind.Outbound.Contains(evt.Kind));
    }

    /// <summary>AC4: an unknown payload property must not throw either.</summary>
    [Fact]
    public void UnknownPayloadProperty_DeserializesWithoutThrowing()
    {
        const string json = """{"activity":"tool","toolName":"Read","someFutureField":"ignored"}""";

        var payload = FleetProtocolJson.Deserialize<TurnProgressPayload>(json);

        Assert.NotNull(payload);
        Assert.Equal(ProgressActivity.Tool, payload!.Activity);
        Assert.Equal("Read", payload.ToolName);
    }

    /// <summary>
    /// AC6. Both of these were listed as v1 kinds in an earlier revision of the design and are
    /// deliberately absent: the out-of-process MCP send path is invisible to the runtime, and
    /// there is no outbound attachment producer. Shipping a kind with no producer is forbidden.
    /// </summary>
    [Theory]
    [InlineData("conversation.notification")]
    [InlineData("attachment.offered")]
    public void ProducerlessKinds_AreAbsentFromV1(string kind)
    {
        Assert.DoesNotContain(kind, ConversationEventKind.Outbound);
        Assert.DoesNotContain(kind, ConversationEventKind.Inbound);

        var declared = typeof(ConversationEventKind)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!);

        Assert.DoesNotContain(kind, declared);
    }

    /// <summary>
    /// #305. <c>submission.text</c> is runtime → client, is not terminal, and is not something a
    /// client sends: the server writes the transcript entry from what it accepted, and a client that
    /// could write one could put words into its own transcript that were never submitted.
    /// </summary>
    [Fact]
    public void SubmissionText_IsAnOutboundNonTerminalKind()
    {
        Assert.Contains(ConversationEventKind.SubmissionText, ConversationEventKind.Outbound);
        Assert.DoesNotContain(ConversationEventKind.SubmissionText, ConversationEventKind.Inbound);
        Assert.DoesNotContain(ConversationEventKind.SubmissionText, ConversationEventKind.Terminal);

        // A separate kind, not a field bolted onto the disposition event.
        Assert.NotEqual(ConversationEventKind.SubmissionAccepted, ConversationEventKind.SubmissionText);
    }

    /// <summary>
    /// #305 backward compatibility: a client built before this kind existed parses the envelope and
    /// ignores it, exactly as the envelope contract already requires of any unknown kind.
    /// </summary>
    /// <remarks>
    /// The assertion is that nothing throws and the envelope is intact — a reader that treated an
    /// unrecognised kind as fatal would break on the first message the owner sent after the upgrade.
    /// </remarks>
    [Fact]
    public void SubmissionText_IsIgnorableByAClientThatDoesNotKnowIt()
    {
        var evt = ConversationEvent.Create(
            ConversationEventKind.SubmissionText, SampleIdentity, "e_1", 7,
            DateTimeOffset.UnixEpoch, new SubmissionTextPayload { Text = "what is open?" });

        var back = FleetProtocolJson.Deserialize<ConversationEvent>(FleetProtocolJson.Serialize(evt));

        Assert.NotNull(back);
        Assert.Equal("submission.text", back!.Kind);
        Assert.Equal(7, back.Seq);
        Assert.False(back.IsTerminal);
        Assert.Equal("what is open?", back.PayloadAs<SubmissionTextPayload>()!.Text);
    }

    /// <summary>The four terminal kinds are exactly the ones that route through the outbox.</summary>
    [Fact]
    public void TerminalKinds_AreExactlyTheFour()
    {
        Assert.Equal(
            new[] { "turn.canceled", "turn.error", "turn.final", "turn.outcome_unknown" },
            ConversationEventKind.Terminal.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// D13 / AC26. No `shutdown` cancel reason ships, because turn cancellation runs on a
    /// per-task token source linked to no host token — a shutdown member would have no producer.
    /// </summary>
    [Fact]
    public void CancelReason_HasNoShutdownMember()
    {
        var names = Enum.GetNames<TurnCancelReason>();
        Assert.DoesNotContain("Shutdown", names);
        Assert.Equal(new[] { "Bridge", "Operator", "Unknown", "User" }, names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Reserved identity fields must never appear on the v1 wire.</summary>
    [Fact]
    public void PolicyScope_IsAbsentFromTheWireWhenNull()
    {
        var json = FleetProtocolJson.Serialize(SampleIdentity);
        Assert.DoesNotContain("policyScope", json);
    }

    /// <summary>Pre-turn events must be expressible: TurnId is nullable and omitted when absent.</summary>
    [Fact]
    public void ForChannel_ProducesAnIdentityWithNoTurn()
    {
        var identity = ConversationIdentity.ForChannel("example-adapter");

        Assert.Null(identity.TurnId);
        Assert.DoesNotContain("turnId", FleetProtocolJson.Serialize(identity));
    }

    /// <summary>
    /// AC7. Identity is immutable: a derivation differs only in TurnId or Attempt and is equal in
    /// every other field. A derived event that RECOMPUTES a field is a defect.
    /// </summary>
    [Fact]
    public void WithTurn_ChangesOnlyTurnId()
    {
        var derived = SampleIdentity.WithTurn("t_new");

        Assert.Equal("t_new", derived.TurnId);
        Assert.Equal(SampleIdentity with { TurnId = "t_new" }, derived);
        Assert.Equal("t_0000000000000001", SampleIdentity.TurnId); // parent untouched
    }

    [Fact]
    public void WithAttempt_ChangesOnlyAttempt()
    {
        var derived = SampleIdentity.WithAttempt(2);

        Assert.Equal(2, derived.Attempt);
        Assert.Equal(SampleIdentity with { Attempt = 2 }, derived);
        Assert.Equal(1, SampleIdentity.Attempt);
    }

    [Fact]
    public void ConversationIdentity_HasNoPublicSetters()
    {
        var settable = typeof(ConversationIdentity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } set && !IsInitOnly(set))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);

        static bool IsInitOnly(MethodInfo setter) =>
            setter.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    /// <summary>AC51 (contract half): every code maps to a fixed message, never runtime text.</summary>
    [Fact]
    public void EveryErrorCode_HasAFixedMessage()
    {
        foreach (var code in Enum.GetValues<ProtocolErrorCode>())
        {
            var message = ProtocolErrors.MessageFor(code);
            Assert.False(string.IsNullOrWhiteSpace(message));
        }
    }

    [Fact]
    public void ProtocolVersion_RejectsAnyOtherVersion()
    {
        Assert.True(ProtocolVersion.IsSupported("fleet.conversation.v1"));
        Assert.False(ProtocolVersion.IsSupported("fleet.conversation.v2"));
        Assert.False(ProtocolVersion.IsSupported(null));
    }

    /// <summary>
    /// AC53. Every kind's serialized property-name set must be a subset of the allowlist. This is
    /// the structural privacy guarantee: a denied field cannot be attached without editing the
    /// contract, and this test is what notices if someone does.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryOutboundKind))]
    public void SerializedPropertyNames_AreASubsetOfTheAllowlist(string kind, object? payload)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // envelope
            "protocol", "eventId", "seq", "emittedAt", "kind", "identity", "payload",
            // identity (D3, minus reserved nulls; CorrelationId is deliberately not a member)
            "principalId", "role", "channelId", "conversationId", "submissionId", "turnId",
            "attempt", "replyToEventId", "policyScope",
            // payload fields enumerated in D5
            "code", "message", "disposition", "queuePosition", "activity", "toolName", "text",
            "truncated", "completion", "isPartial", "mergedSubmissionIds", "reason", "target",
            "accepted", "hadRunningTask",
        };

        var evt = ConversationEvent.Create(kind, SampleIdentity, "e_1", 1, DateTimeOffset.UnixEpoch, (object?)payload);
        var node = JsonNode.Parse(FleetProtocolJson.Serialize(evt))!;

        foreach (var name in CollectPropertyNames(node))
            Assert.Contains(name, allowed);
    }

    private static IEnumerable<string> CollectPropertyNames(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    yield return name;
                    foreach (var nested in CollectPropertyNames(child))
                        yield return nested;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var nested in CollectPropertyNames(item))
                        yield return nested;
                break;
        }
    }
}
