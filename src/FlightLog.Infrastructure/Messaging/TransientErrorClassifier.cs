using System.Net.Sockets;
using Amazon.Runtime;
using FlightLog.Application.Abstractions;
using Npgsql;

namespace FlightLog.Infrastructure.Messaging;

/// <summary>Failures of the database, object storage or network that are worth retrying.</summary>
internal sealed class TransientErrorClassifier : ITransientErrorClassifier
{
    public bool IsTransient(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case NpgsqlException npgsql when npgsql.IsTransient:
                case TimeoutException:
                case SocketException:
                case HttpRequestException:
                case AmazonServiceException { StatusCode: >= System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.TooManyRequests }:
                    return true;
                case AmazonServiceException:
                    // Answered by the service (missing object, access denied): retrying does not help.
                    break;
                case AmazonClientException:
                    // The request did not get an answer (network, endpoint unreachable).
                    return true;
                case Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException:
                    return true;
            }
        }

        return false;
    }
}
