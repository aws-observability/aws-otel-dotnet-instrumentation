using Xunit;
namespace AWS.OpenTelemetry.AutoInstrumentation.Tests;

public class SqlUrlParserTest
{
    [Fact]
    public void testSqsClientSpanBasicUrls() {
        validate("https://sqs.us-east-1.amazonaws.com/123412341234/Q_Name-5", "Q_Name-5");
        validate("https://sqs.af-south-1.amazonaws.com/999999999999/-_ThisIsValid", "-_ThisIsValid");
        validate("http://sqs.eu-west-3.amazonaws.com/000000000000/FirstQueue", "FirstQueue");
        validate("sqs.sa-east-1.amazonaws.com/123456781234/SecondQueue", "SecondQueue");
    }
    
    [Fact]
    public void testSqsClientSpanCustomUrls() {
        validate("http://127.0.0.1:1212/123456789012/MyQueue", "MyQueue");
        validate("https://127.0.0.1:1212/123412341234/RRR", "RRR");
        validate("127.0.0.1:1212/123412341234/QQ", "QQ");
        validate("https://amazon.com/123412341234/BB", "BB");
    }
    
    [Fact]
    public void testSqsClientSpanLegacyFormatUrls() {
        validate("https://ap-northeast-2.queue.amazonaws.com/123456789012/MyQueue", "MyQueue");
        validate("http://cn-northwest-1.queue.amazonaws.com/123456789012/MyQueue", "MyQueue");
        validate("http://cn-north-1.queue.amazonaws.com/123456789012/MyQueue", "MyQueue");
        validate(
            "ap-south-1.queue.amazonaws.com/123412341234/MyLongerQueueNameHere",
            "MyLongerQueueNameHere");
        validate("https://queue.amazonaws.com/123456789012/MyQueue", "MyQueue");
    }

    [Fact]
    public void testSqsClientSpanLongUrls()
    {
        string queueName = string.Concat(Enumerable.Repeat("a", 80));
        validate("http://127.0.0.1:1212/123456789012/" + queueName, queueName);

        string queueNameTooLong = string.Concat(Enumerable.Repeat("a", 81));
        validate("http://127.0.0.1:1212/123456789012/" + queueNameTooLong, null);
    }

    [Fact]
    public void testClientSpanSqsInvalidOrEmptyUrls() {
        validate(null, null);
        validate("", null);
        validate(" ", null);
        validate("/", null);
        validate("//", null);
        validate("///", null);
        validate("//asdf", null);
        validate("/123412341234/as&df", null);
        validate("invalidUrl", null);
        validate("https://www.amazon.com", null);
        validate("https://sqs.us-east-1.amazonaws.com/123412341234/.", null);
        validate("https://sqs.us-east-1.amazonaws.com/12/Queue", null);
        validate("https://sqs.us-east-1.amazonaws.com/A/A", null);
        validate("https://sqs.us-east-1.amazonaws.com/123412341234/A/ThisShouldNotBeHere", null);
    }
    private void validate(string url, string? expectedName) {
        Assert.Equal(SqsUrlParser.GetQueueName(url), expectedName);
    }
}
