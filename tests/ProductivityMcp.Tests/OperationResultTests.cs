using System.Text.Json;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;
using ProductivityMcp.Server;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class OperationResultTests
{
    [TestMethod]
    public void RequiredObjectNull_ReturnsNamedValidationError()
    {
        var result = ModelValidation.Validate<EventQuery>(null, "query");

        Assert.IsInstanceOfType<OperationResult<EventQuery>.Failure>(result);
        var error = ((OperationResult<EventQuery>.Failure)result).Error;
        Assert.AreEqual(OperationErrorCode.Validation, error.Code);
        Assert.AreEqual("query", error.Field);
        Assert.AreEqual("query is required.", error.Message);
    }

    [TestMethod]
    public void DateOnlyJsonException_ReturnsRequiredFormat()
    {
        var exception = Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<DateOnly>("\"2037-04-02T00:00:00Z\""));

        var error = McpToolResults.InvalidJson(exception);

        Assert.AreEqual(OperationErrorCode.Validation, error.Code);
        StringAssert.Contains(error.Message, "must be yyyy-MM-dd");
    }

    [TestMethod]
    public void McpErrorResult_HasStableStructuredSignature()
    {
        var result = McpToolResults.Error(new OperationError(
            OperationErrorCode.RateLimited,
            "Retry later.",
            Retryable: true,
            RetryAfterSeconds: 30));

        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.StructuredContent.HasValue);
        var error = result.StructuredContent.Value.GetProperty("error");
        Assert.AreEqual("rate_limited", error.GetProperty("code").GetString());
        Assert.AreEqual("Retry later.", error.GetProperty("message").GetString());
        Assert.IsTrue(error.GetProperty("retryable").GetBoolean());
        Assert.AreEqual(30, error.GetProperty("retryAfterSeconds").GetInt32());
    }

    [TestMethod]
    public void GoogleAdapter_TranslatesExpectedFailuresOnly()
    {
        var configuration = GoogleOperation.Translate(new FileNotFoundException());
        var unavailable = GoogleOperation.Translate(new HttpRequestException());
        var unknown = GoogleOperation.Translate(new InvalidOperationException("programming bug"));

        Assert.AreEqual(OperationErrorCode.Configuration, configuration?.Code);
        Assert.AreEqual(OperationErrorCode.ProviderUnavailable, unavailable?.Code);
        Assert.IsTrue(unavailable?.Retryable);
        Assert.IsNull(unknown);
    }
}
