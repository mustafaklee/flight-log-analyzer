using System.Net;
using System.Net.Sockets;
using Amazon.S3;
using FlightLog.Infrastructure.Messaging;

namespace FlightLog.UnitTests.Analysis;

public sealed class TransientErrorClassifierTests
{
    private static readonly TransientErrorClassifier Classifier = new();

    [Fact]
    public void Infrastructure_failures_are_transient()
    {
        Classifier.IsTransient(new TimeoutException()).Should().BeTrue();
        Classifier.IsTransient(new HttpRequestException("down")).Should().BeTrue();
        Classifier.IsTransient(new InvalidOperationException("wrapped", new SocketException())).Should().BeTrue();
        Classifier.IsTransient(new AmazonS3Exception("busy") { StatusCode = HttpStatusCode.ServiceUnavailable }).Should().BeTrue();
    }

    [Fact]
    public void Answers_and_bugs_are_not()
    {
        Classifier.IsTransient(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound }).Should().BeFalse();
        Classifier.IsTransient(new ArgumentException("bug")).Should().BeFalse();
        Classifier.IsTransient(new InvalidDataException()).Should().BeFalse();
    }
}
