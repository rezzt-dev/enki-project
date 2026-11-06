using System;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Las líneas de estos tests están copiadas (recortadas) de la salida real de
/// <c>claude -p --output-format stream-json --verbose --include-partial-messages</c> 2.1.x, para que un
/// cambio del parser se pruebe contra lo que el CLI emite de verdad y no contra lo que se supone.</summary>
public class ClaudeStreamParserTests
{
    [Fact]
    public void Parse_SystemInit_ReturnsSessionAndModel()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"system","subtype":"init","cwd":"/tmp/x","session_id":"1722778c-f815-4844-8ad6-c63d3cc4173b","tools":[],"mcp_servers":[],"model":"claude-haiku-4-5-20251001","permissionMode":"default"}""");

        var started = Assert.IsType<ClaudeSessionStarted>(parsed);
        Assert.Equal("1722778c-f815-4844-8ad6-c63d3cc4173b", started.SessionId);
        Assert.Equal("claude-haiku-4-5-20251001", started.Model);
    }

    [Fact]
    public void Parse_TextDelta_ReturnsFragment()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"hola"}},"session_id":"s","parent_tool_use_id":null,"uuid":"u"}""");

        Assert.Equal("hola", Assert.IsType<ClaudeTextDelta>(parsed).Text);
    }

    [Fact]
    public void Parse_ThinkingBlockStart_IsReportedWithoutContent()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}},"session_id":"s","parent_tool_use_id":null}""");

        Assert.IsType<ClaudeThinkingStarted>(parsed);
    }

    [Fact]
    public void Parse_ThinkingAndSignatureDeltas_AreIgnored()
    {
        Assert.Null(ClaudeStreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"secreto"}},"session_id":"s","parent_tool_use_id":null}"""));
        Assert.Null(ClaudeStreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EowE"}},"session_id":"s","parent_tool_use_id":null}"""));
    }

    [Fact]
    public void Parse_AssistantMessage_ConcatenatesTextBlocksOnly()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"x"},{"type":"text","text":"Hola "},{"type":"text","text":"mundo"}]},"session_id":"s","parent_tool_use_id":null}""");

        var message = Assert.IsType<ClaudeAssistantMessage>(parsed);
        Assert.Equal("Hola mundo", message.Text);
        Assert.Null(message.Error);
    }

    [Fact]
    public void Parse_SyntheticErrorMessage_CarriesErrorCode()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"assistant","message":{"model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"There's an issue with the selected model (x)."}]},"parent_tool_use_id":null,"session_id":"s","error":"model_not_found"}""");

        Assert.Equal("model_not_found", Assert.IsType<ClaudeAssistantMessage>(parsed).Error);
    }

    [Fact]
    public void Parse_SuccessResult()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"result","subtype":"success","is_error":false,"result":"ok","session_id":"abc","api_error_status":null,"terminal_reason":"completed","num_turns":1}""");

        var result = Assert.IsType<ClaudeResult>(parsed);
        Assert.False(result.IsError);
        Assert.Equal("ok", result.Text);
        Assert.Equal("abc", result.SessionId);
        Assert.Null(result.ApiErrorStatus);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_ErrorResult_ReadsErrorsAndStatus()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":0,"session_id":"00000000-0000-4000-8000-000000000000","errors":["No conversation found with session ID: 00000000-0000-4000-8000-000000000000"]}""");

        var result = Assert.IsType<ClaudeResult>(parsed);
        Assert.True(result.IsError);
        Assert.Equal("error_during_execution", result.Subtype);
        Assert.Single(result.Errors);

        var withStatus = Assert.IsType<ClaudeResult>(ClaudeStreamParser.Parse(
            """{"type":"result","subtype":"success","is_error":true,"result":"model issue","api_error_status":404,"terminal_reason":"api_error"}"""));
        Assert.Equal(404, withStatus.ApiErrorStatus);
        Assert.Equal("api_error", withStatus.TerminalReason);
    }

    [Fact]
    public void Parse_RateLimitEvent_ReadsStatusAndReset()
    {
        var parsed = ClaudeStreamParser.Parse(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792888200,"rateLimitType":"five_hour"},"uuid":"u","session_id":"s"}""");

        var rateLimit = Assert.IsType<ClaudeRateLimit>(parsed);
        Assert.True(rateLimit.IsRejected);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792888200), rateLimit.ResetsAt);

        var allowed = Assert.IsType<ClaudeRateLimit>(ClaudeStreamParser.Parse(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792888200}}"""));
        Assert.False(allowed.IsRejected);
    }

    [Fact]
    public void Parse_SubagentEvents_AreIgnored()
    {
        Assert.Null(ClaudeStreamParser.Parse(
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"x"}},"parent_tool_use_id":"toolu_1"}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ broken json")]
    [InlineData("""{"type":"system","subtype":"status","status":"requesting"}""")]
    [InlineData("""{"type":"something_new","payload":1}""")]
    public void Parse_UnknownOrMalformedLines_ReturnNull(string line) => Assert.Null(ClaudeStreamParser.Parse(line));
}
