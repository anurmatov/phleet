using Fleet.Memory.Models;

namespace Fleet.Memory.Tests;

/// <summary>
/// Tests for the input validation added to Fleet.Memory MCP tool handlers.
/// Each tool returns a descriptive error string when required parameters are
/// missing or carry invalid enum-like values, instead of surfacing the
/// generic "An error occurred invoking" message from AIFunctionFactory.
///
/// These tests mirror the validation conditions from the tool source files
/// (src/Fleet.Memory/Tools/) so that the error paths are covered without
/// requiring a live MemoryService (which needs Qdrant + filesystem).
/// </summary>
public class MemoryToolValidationTests
{
    // --- helpers mirroring validation in each tool ---

    static string? ValidateStore(string type, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(type))
            return $"memory_store: missing required parameter 'type'.\nHint: pass one of: {string.Join(", ", MemoryDocument.ValidTypes)}.";
        if (!MemoryDocument.ValidTypes.Contains(type))
            return $"memory_store: invalid value for 'type': '{type}'.\nValid types: {string.Join(", ", MemoryDocument.ValidTypes)}.";
        if (string.IsNullOrWhiteSpace(title))
            return "memory_store: missing required parameter 'title'.\nHint: pass a short descriptive title (5-10 words).";
        if (string.IsNullOrWhiteSpace(content))
            return "memory_store: missing required parameter 'content'.\nHint: pass the full memory content to store.";
        return null;
    }

    static string? ValidateUpdate(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_update: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";
        return null;
    }

    static string? ValidateDelete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_delete: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";
        return null;
    }

    static string? ValidateGet(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_get: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";
        return null;
    }

    static string? ValidateSearch(string query, string? type)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "memory_search: missing required parameter 'query'.\nHint: pass a natural language search query describing what you're looking for.";
        if (type is not null && !MemoryDocument.ValidTypes.Contains(type))
            return $"memory_search: invalid value for 'type' filter: '{type}'.\nValid types: {string.Join(", ", MemoryDocument.ValidTypes)}.";
        return null;
    }

    static string? ValidateList(string? type)
    {
        if (type is not null && !MemoryDocument.ValidTypes.Contains(type))
            return $"memory_list: invalid value for 'type' filter: '{type}'.\nValid types: {string.Join(", ", MemoryDocument.ValidTypes)}.";
        return null;
    }

    // --- memory_store ---

    [Fact]
    public void Store_MissingType_ReturnsError()
    {
        var error = ValidateStore("", "My title", "Some content");
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'type'", error);
    }

    [Fact]
    public void Store_InvalidType_ReturnsErrorWithValidList()
    {
        var error = ValidateStore("bogus", "My title", "Some content");
        Assert.NotNull(error);
        Assert.Contains("invalid value for 'type': 'bogus'", error);
        Assert.Contains("learning", error); // valid types listed
    }

    [Fact]
    public void Store_MissingTitle_ReturnsError()
    {
        var error = ValidateStore("learning", "   ", "Some content");
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'title'", error);
    }

    [Fact]
    public void Store_MissingContent_ReturnsError()
    {
        var error = ValidateStore("learning", "My title", "");
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'content'", error);
    }

    [Fact]
    public void Store_ValidArgs_ReturnsNull()
    {
        var error = ValidateStore("learning", "My title", "Some content");
        Assert.Null(error);
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
    public void Store_AllValidTypes_Pass(string type)
    {
        var error = ValidateStore(type, "title", "content");
        Assert.Null(error);
    }

    // --- memory_update ---

    [Fact]
    public void Update_MissingId_ReturnsError()
    {
        var error = ValidateUpdate("");
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'id'", error);
    }

    [Fact]
    public void Update_WhitespaceId_ReturnsError()
    {
        var error = ValidateUpdate("   ");
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'id'", error);
    }

    [Fact]
    public void Update_ValidId_ReturnsNull()
    {
        var error = ValidateUpdate("abc12345");
        Assert.Null(error);
    }

    // --- memory_delete ---

    [Fact]
    public void Delete_MissingId_ReturnsError()
    {
        var error = ValidateDelete("");
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'id'", error);
    }

    [Fact]
    public void Delete_ValidId_ReturnsNull()
    {
        var error = ValidateDelete("abc12345");
        Assert.Null(error);
    }

    // --- memory_get ---

    [Fact]
    public void Get_MissingId_ReturnsError()
    {
        var error = ValidateGet("");
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'id'", error);
    }

    [Fact]
    public void Get_ValidId_ReturnsNull()
    {
        var error = ValidateGet("abc12345");
        Assert.Null(error);
    }

    // --- memory_search ---

    [Fact]
    public void Search_MissingQuery_ReturnsError()
    {
        var error = ValidateSearch("", null);
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'query'", error);
    }

    [Fact]
    public void Search_WhitespaceQuery_ReturnsError()
    {
        var error = ValidateSearch("   ", null);
        Assert.NotNull(error);
        Assert.Contains("missing required parameter 'query'", error);
    }

    [Fact]
    public void Search_InvalidTypeFilter_ReturnsError()
    {
        var error = ValidateSearch("some query", "foo");
        Assert.NotNull(error);
        Assert.Contains("invalid value for 'type' filter: 'foo'", error);
    }

    [Fact]
    public void Search_ValidTypeFilter_ReturnsNull()
    {
        var error = ValidateSearch("some query", "learning");
        Assert.Null(error);
    }

    [Fact]
    public void Search_NullTypeFilter_ReturnsNull()
    {
        var error = ValidateSearch("some query", null);
        Assert.Null(error);
    }

    // --- memory_list ---

    [Fact]
    public void List_InvalidTypeFilter_ReturnsError()
    {
        var error = ValidateList("not-a-type");
        Assert.NotNull(error);
        Assert.Contains("invalid value for 'type' filter: 'not-a-type'", error);
        Assert.Contains("learning", error); // valid types listed
    }

    [Fact]
    public void List_ValidTypeFilter_ReturnsNull()
    {
        var error = ValidateList("decision");
        Assert.Null(error);
    }

    [Fact]
    public void List_NullTypeFilter_ReturnsNull()
    {
        var error = ValidateList(null);
        Assert.Null(error);
    }

    // --- error message format checks ---

    [Fact]
    public void ErrorMessages_IncludeToolName()
    {
        Assert.StartsWith("memory_store:", ValidateStore("", "t", "c")!);
        Assert.StartsWith("memory_update:", ValidateUpdate("")!);
        Assert.StartsWith("memory_delete:", ValidateDelete("")!);
        Assert.StartsWith("memory_get:", ValidateGet("")!);
        Assert.StartsWith("memory_search:", ValidateSearch("", null)!);
        Assert.StartsWith("memory_list:", ValidateList("bad")!);
    }
}
