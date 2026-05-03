using Fleet.Memory.Configuration;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Tests;

/// <summary>
/// Tests for the input validation added to Fleet.Memory MCP tool handlers.
/// Each tool returns a descriptive error string when required parameters are
/// missing or carry invalid enum-like values, instead of surfacing the
/// generic "An error occurred invoking" message from AIFunctionFactory.
///
/// Tools are constructed with null! for MemoryService. Validation returns
/// before the service is ever touched, so the null is never dereferenced.
/// </summary>
public class MemoryToolValidationTests
{
    // Disabled ACL service for tool construction in validation tests
    private static AclCacheService DisabledAcl()
    {
        var opts = Options.Create(new AclOptions { EnableProjectScopedAcl = false });
        var oOpts = Options.Create(new OrchestratorOptions());
        var svc = new AclCacheService(opts, oOpts, NullLogger<AclCacheService>.Instance);
        svc.InjectAclForTesting([]);
        return svc;
    }

    // --- memory_store ---

    [Fact]
    public async Task Store_MissingType_ReturnsError()
    {
        var result = await new MemoryStoreTool(null!).StoreAsync("", "My title", "Some content");
        Assert.Contains("missing required parameter 'type'", result);
    }

    [Fact]
    public async Task Store_InvalidType_ReturnsErrorWithValidList()
    {
        var result = await new MemoryStoreTool(null!).StoreAsync("bogus", "My title", "Some content");
        Assert.Contains("invalid value for 'type': 'bogus'", result);
        Assert.Contains("learning", result);
    }

    [Fact]
    public async Task Store_MissingTitle_ReturnsError()
    {
        var result = await new MemoryStoreTool(null!).StoreAsync("learning", "   ", "Some content");
        Assert.Contains("missing required parameter 'title'", result);
    }

    [Fact]
    public async Task Store_MissingContent_ReturnsError()
    {
        var result = await new MemoryStoreTool(null!).StoreAsync("learning", "My title", "");
        Assert.Contains("missing required parameter 'content'", result);
    }

    [Theory]
    [InlineData("task_result")]
    [InlineData("learning")]
    [InlineData("decision")]
    [InlineData("reference")]
    [InlineData("error_resolution")]
    [InlineData("codebase_knowledge")]
    [InlineData("user_preference")]
    [InlineData("conversation_summary")]
    public async Task Store_AllValidTypes_PassValidation(string type)
    {
        // Validation passes → service is called → NullReferenceException (null! dep).
        // Any other exception or a validation-error string means the type was rejected.
        try
        {
            var result = await new MemoryStoreTool(null!).StoreAsync(type, "title", "content");
            Assert.DoesNotContain("invalid value for 'type'", result);
        }
        catch (NullReferenceException)
        {
            // Expected: validation passed, service call reached.
        }
    }

    // --- memory_update ---

    [Fact]
    public async Task Update_MissingId_ReturnsError()
    {
        var result = await new MemoryUpdateTool(null!).UpdateAsync("");
        Assert.Contains("missing required parameter 'id'", result);
    }

    [Fact]
    public async Task Update_WhitespaceId_ReturnsError()
    {
        var result = await new MemoryUpdateTool(null!).UpdateAsync("   ");
        Assert.Contains("missing required parameter 'id'", result);
    }

    // --- memory_delete ---

    [Fact]
    public async Task Delete_MissingId_ReturnsError()
    {
        var result = await new MemoryDeleteTool(null!).DeleteAsync("");
        Assert.Contains("missing required parameter 'id'", result);
    }

    [Fact]
    public async Task Delete_WhitespaceId_ReturnsError()
    {
        var result = await new MemoryDeleteTool(null!).DeleteAsync("   ");
        Assert.Contains("missing required parameter 'id'", result);
    }

    // --- memory_get ---

    [Fact]
    public async Task Get_MissingId_ReturnsError()
    {
        var result = await new MemoryGetTool(null!, new ReadCounterService(), DisabledAcl(), new HttpContextAccessor(), NullLogger<MemoryGetTool>.Instance).GetAsync("");
        Assert.Contains("missing required parameter 'id'", result);
    }

    [Fact]
    public async Task Get_WhitespaceId_ReturnsError()
    {
        var result = await new MemoryGetTool(null!, new ReadCounterService(), DisabledAcl(), new HttpContextAccessor(), NullLogger<MemoryGetTool>.Instance).GetAsync("   ");
        Assert.Contains("missing required parameter 'id'", result);
    }

    // --- memory_search ---

    [Fact]
    public async Task Search_MissingQuery_ReturnsError()
    {
        var result = await new MemorySearchTool(null!, DisabledAcl(), new HttpContextAccessor()).SearchAsync("");
        Assert.Contains("missing required parameter 'query'", result);
    }

    [Fact]
    public async Task Search_WhitespaceQuery_ReturnsError()
    {
        var result = await new MemorySearchTool(null!, DisabledAcl(), new HttpContextAccessor()).SearchAsync("   ");
        Assert.Contains("missing required parameter 'query'", result);
    }

    [Fact]
    public async Task Search_InvalidTypeFilter_ReturnsError()
    {
        var result = await new MemorySearchTool(null!, DisabledAcl(), new HttpContextAccessor()).SearchAsync("some query", type: "foo");
        Assert.Contains("invalid value for 'type' filter: 'foo'", result);
        Assert.Contains("learning", result);
    }

    // --- memory_list ---

    [Fact]
    public async Task List_InvalidTypeFilter_ReturnsError()
    {
        var result = await new MemoryListTool(null!, DisabledAcl(), new HttpContextAccessor()).ListAsync(type: "not-a-type");
        Assert.Contains("invalid value for 'type' filter: 'not-a-type'", result);
        Assert.Contains("learning", result);
    }

    // --- error message format ---

    [Fact]
    public async Task ErrorMessages_IncludeToolName()
    {
        Assert.StartsWith("memory_store:", await new MemoryStoreTool(null!).StoreAsync("", "t", "c"));
        Assert.StartsWith("memory_update:", await new MemoryUpdateTool(null!).UpdateAsync(""));
        Assert.StartsWith("memory_delete:", await new MemoryDeleteTool(null!).DeleteAsync(""));
        Assert.StartsWith("memory_get:", await new MemoryGetTool(null!, new ReadCounterService(), DisabledAcl(), new HttpContextAccessor(), NullLogger<MemoryGetTool>.Instance).GetAsync(""));
        Assert.StartsWith("memory_search:", await new MemorySearchTool(null!, DisabledAcl(), new HttpContextAccessor()).SearchAsync(""));
        Assert.StartsWith("memory_list:", await new MemoryListTool(null!, DisabledAcl(), new HttpContextAccessor()).ListAsync(type: "bad"));
    }
}
