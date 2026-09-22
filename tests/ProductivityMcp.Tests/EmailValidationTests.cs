using System.Text.Json;
using System.Text.Json.Nodes;
using ProductivityMcp.Core;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class EmailValidationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow("to", "null")]
    [DataRow("cc", "null")]
    [DataRow("bcc", "null")]
    [DataRow("to", "[null]")]
    [DataRow("to", "[\"\"]")]
    [DataRow("subject", "null")]
    [DataRow("body", "null")]
    [DataRow("body", "{\"format\":\"html\",\"content\":null}")]
    [DataRow("body", "{\"format\":99,\"content\":\"test\"}")]
    [DataRow("attachments", "null")]
    [DataRow("attachments", "[null]")]
    [DataRow("attachments", "[{\"path\":null}]")]
    [DataRow("attachments", "[{\"path\":\" \"}]")]
    public void Send_InvalidFieldsReturnValidationError(string field, string json)
    {
        var input = JsonNode.Parse("""{"to":["test@example.com"],"body":{"format":"html","content":"test"}}""")!;
        input[field] = JsonNode.Parse(json);
        var message = input.Deserialize<OutgoingEmail>(Json);

        var failure = Assert.IsInstanceOfType<OperationResult<OutgoingEmail>.Failure>(ModelValidation.Validate(message, "message"));
        Assert.AreEqual(OperationErrorCode.Validation, failure.Error.Code);
        Assert.AreEqual(field, failure.Error.Field);
    }

    [TestMethod]
    public void DraftPatch_NullClearsButInvalidNestedContentIsRejected()
    {
        var clear = JsonSerializer.Deserialize<EmailDraftPatch>("""{"to":null,"body":null,"attachments":null}""", Json);
        Assert.IsInstanceOfType<OperationResult<EmailDraftPatch>.Success>(ModelValidation.Validate(clear, "patch"));
        var invalid = JsonSerializer.Deserialize<EmailDraftPatch>("""{"body":{"format":"html","content":null}}""", Json);
        Assert.IsInstanceOfType<OperationResult<EmailDraftPatch>.Failure>(ModelValidation.Validate(invalid, "patch"));
    }

    [TestMethod]
    public void PatchAndQuery_NullCollectionsAndChildrenAreRejected()
    {
        var patch = JsonSerializer.Deserialize<EmailMessagePatch>("""{"unread":true,"addLabels":null}""", Json);
        Assert.IsInstanceOfType<OperationResult<EmailMessagePatch>.Failure>(ModelValidation.Validate(patch, "patch"));
        var query = JsonSerializer.Deserialize<EmailQuery>("""{"filter":{"and":[null]}}""", Json);
        Assert.IsInstanceOfType<OperationResult<EmailQuery>.Failure>(ModelValidation.Validate(query, "query"));
    }
}
