using System.Net;
using System.Net.Http.Json;
using FlightLog.Application.Collaboration;
using FlightLog.Infrastructure.Persistence;
using FlightLog.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightLog.IntegrationTests;

/// <summary>Comments, time-range notes, finding reviews, tags and the audit trail, with the roles of a real team.</summary>
public sealed class CollaborationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Comments_and_notes_follow_the_team_roles()
    {
        var team = await CreateTeamAsync();
        var note = await (await team.Engineer.PostAsJsonAsync($"/api/v1/flights/{team.FlightId}/comments",
            new { body = "GPS drops here, check the antenna.", startUs = 38_409_630L, endUs = 48_809_635L }, Ct)).ReadAsync<Comment>(HttpStatusCode.Created);
        note.StartUs.Should().Be(38_409_630);
        note.Author.DisplayName.Should().Be("Engin Engineer");
        note.CanEdit.Should().BeTrue();
        var general = await (await team.Owner.PostAsJsonAsync($"/api/v1/flights/{team.FlightId}/comments", new { body = "Acceptance flight." }, Ct))
            .ReadAsync<Comment>(HttpStatusCode.Created);
        general.StartUs.Should().BeNull();

        var seenByViewer = await (await team.Viewer.GetAsync($"/api/v1/flights/{team.FlightId}/comments", Ct)).ReadAsync<Comment[]>(HttpStatusCode.OK);
        seenByViewer.Select(c => c.Id).Should().Equal(note.Id, general.Id);
        seenByViewer.Should().OnlyContain(c => !c.CanEdit && !c.CanDelete);
        (await team.Viewer.PostAsJsonAsync($"/api/v1/flights/{team.FlightId}/comments", new { body = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Outsider.GetAsync($"/api/v1/flights/{team.FlightId}/comments", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await team.Outsider.PostAsJsonAsync($"/api/v1/flights/{team.FlightId}/comments", new { body = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await team.Engineer.PostAsJsonAsync($"/api/v1/flights/{team.FlightId}/comments", new { body = "x", startUs = 10L, endUs = 5L }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await team.Engineer.PostAsJsonAsync($"/api/v1/flights/{team.FlightId}/comments", new { body = " " }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Only the author edits; the version guards against overwriting someone else's edit.
        (await team.Owner.PutAsJsonAsync($"/api/v1/comments/{note.Id}", new { body = "changed", version = note.Version }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Engineer.PutAsJsonAsync($"/api/v1/comments/{note.Id}", new { body = "changed", version = note.Version + 1 }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var edited = await (await team.Engineer.PutAsJsonAsync($"/api/v1/comments/{note.Id}", new { body = "GPS drops: antenna cable loose.", version = note.Version }, Ct))
            .ReadAsync<Comment>(HttpStatusCode.OK);
        edited.EditedAt.Should().NotBeNull();
        (await team.Outsider.PutAsJsonAsync($"/api/v1/comments/{note.Id}", new { body = "x", version = edited.Version }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Authors and project managers delete; the comment stays in the thread without its text.
        (await team.Engineer.DeleteAsync($"/api/v1/comments/{general.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Owner.DeleteAsync($"/api/v1/comments/{note.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await (await team.Engineer.GetAsync($"/api/v1/flights/{team.FlightId}/comments", Ct)).ReadAsync<Comment[]>(HttpStatusCode.OK);
        var deleted = after.Single(c => c.Id == note.Id);
        deleted.Deleted.Should().BeTrue();
        deleted.Body.Should().BeNull();
        deleted.CanEdit.Should().BeFalse();
        (await team.Engineer.PutAsJsonAsync($"/api/v1/comments/{note.Id}", new { body = "again", version = edited.Version + 1 }, Ct))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity);

        var activity = await (await team.Viewer.GetAsync($"/api/v1/flights/{team.FlightId}/activity?action=comment.", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        activity.Items.Select(a => a.Action).Should().Equal("comment.deleted", "comment.edited", "comment.created", "comment.created");
        var removal = activity.Items[0];
        removal.Actor.DisplayName.Should().Be("Pam Manager");
        removal.Data["body"].Should().Be("GPS drops: antenna cable loose.", "moderation stays reviewable");
        activity.Items[1].Data.Should().Contain("from", "GPS drops here, check the antenna.");
    }

    [Fact]
    public async Task A_finding_review_follows_the_finding_across_reanalysis()
    {
        var team = await CreateTeamAsync();
        var findings = await FindingsAsync(team.Engineer, team.RunId);
        var gap = findings.Single(f => f.RuleId == "data.gaps");
        gap.Review.Should().BeNull("nobody has reviewed it yet");

        var review = await (await team.Engineer.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review",
            new { status = "Investigating", assigneeId = team.EngineerId, flagged = true, note = "Logger dropout or radio? Checking the SD card." }, Ct))
            .ReadAsync<Review>(HttpStatusCode.OK);
        review.Status.Should().Be("Investigating");
        review.Assignee!.Id.Should().Be(team.EngineerId);
        review.Flagged.Should().BeTrue();
        review.CommentCount.Should().Be(1);
        review.UpdatedBy.DisplayName.Should().Be("Engin Engineer");

        // Assignees must be able to act on the finding, and must belong to the team.
        var readOnly = await (await team.Engineer.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review", new { assigneeId = team.ViewerId }, Ct))
            .ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity);
        readOnly.Code.Should().Be("review.assignee_read_only");
        var notMember = await (await team.Engineer.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review", new { assigneeId = team.OutsiderId }, Ct))
            .ReadAsync<Problem>(HttpStatusCode.UnprocessableEntity);
        notMember.Code.Should().Be("review.assignee_not_member");
        (await team.Engineer.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review", new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await team.Engineer.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review", new { assigneeId = team.EngineerId, unassign = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await team.Viewer.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review", new { status = "Dismissed" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Outsider.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review", new { status = "Dismissed" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await team.Outsider.GetAsync($"/api/v1/findings/{gap.Id}/history", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var listed = await FindingsAsync(team.Viewer, team.RunId);
        listed.Single(f => f.Id == gap.Id).Review!.Status.Should().Be("Investigating");
        (await FindingsAsync(team.Viewer, team.RunId, "reviewStatus=Investigating")).Select(f => f.Id).Should().Equal(gap.Id);
        (await FindingsAsync(team.Viewer, team.RunId, "reviewStatus=Open")).Should().HaveCount(findings.Length - 1);
        (await FindingsAsync(team.Viewer, team.RunId, $"assigneeId={team.EngineerId}")).Select(f => f.Id).Should().Equal(gap.Id);
        (await FindingsAsync(team.Viewer, team.RunId, "flagged=true")).Select(f => f.Id).Should().Equal(gap.Id);

        await team.Owner.PatchAsJsonAsync($"/api/v1/findings/{gap.Id}/review", new { status = "Confirmed", unassign = true }, Ct);
        var history = await (await team.Viewer.GetAsync($"/api/v1/findings/{gap.Id}/history", Ct)).ReadAsync<AuditEntry[]>(HttpStatusCode.OK);
        history.Select(h => h.Action).Should().Equal(
            "finding.assigned", "finding.status_changed", "comment.created", "finding.flagged", "finding.assigned", "finding.status_changed");
        history[1].Data.Should().Contain("from", "Investigating").And.Contain("to", "Confirmed");
        history[1].Actor.DisplayName.Should().Be("Pam Manager");
        history[^1].Data.Should().Contain("from", "Open");
        history[0].Data.Should().Contain("fromName", "Engin Engineer").And.Contain("toName", null);

        // Re-analysis writes new findings; the review is found again because the rule reports the same time range.
        (await team.Engineer.PostAsync($"/api/v1/flights/{team.FlightId}/analysis-runs", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await factory.ProcessQueueAsync();
        var runs = await (await team.Viewer.GetAsync($"/api/v1/flights/{team.FlightId}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK);
        var newRun = runs.First(r => r.Id != team.RunId && r.Status == "Succeeded");
        var again = (await FindingsAsync(team.Viewer, newRun.Id)).Single(f => f.RuleId == "data.gaps");
        again.Id.Should().NotBe(gap.Id);
        again.Review!.Id.Should().Be(review.Id);
        again.Review.Status.Should().Be("Confirmed");
        again.Review.Assignee.Should().BeNull();
        var thread = await (await team.Viewer.GetAsync($"/api/v1/flights/{team.FlightId}/comments?findingId={again.Id}", Ct)).ReadAsync<Comment[]>(HttpStatusCode.OK);
        thread.Single().Body.Should().StartWith("Logger dropout");

        var work = await (await team.Viewer.GetAsync($"/api/v1/teams/{team.TeamId}/finding-reviews?status=Confirmed", Ct)).ReadAsync<Paged<ReviewItem>>(HttpStatusCode.OK);
        var item = work.Items.Single();
        item.FindingId.Should().Be(again.Id, "the work list points at the finding of the latest analysis");
        item.Title.Should().Be(again.Title);
        item.FlightName.Should().Be("sitl");
        (await team.Outsider.GetAsync($"/api/v1/teams/{team.TeamId}/finding-reviews", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tags_label_flights_and_filter_the_history()
    {
        var team = await CreateTeamAsync();
        var tagged = await (await team.Engineer.PutAsJsonAsync($"/api/v1/flights/{team.FlightId}/tags", new { tags = MixedTags }, Ct))
            .ReadAsync<TaggedFlight>(HttpStatusCode.OK);
        tagged.Tags.Should().Equal("acceptance", "GPS test");
        (await team.Viewer.PutAsJsonAsync($"/api/v1/flights/{team.FlightId}/tags", new { tags = OneTag }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Engineer.PutAsJsonAsync($"/api/v1/flights/{team.FlightId}/tags", new { tags = TooLongTag }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var other = await (await team.Engineer.UploadLogAsync(team.ProjectId, Fixtures.ULogLike(77), "other.ulg")).ReadAsync<UploadResult>(HttpStatusCode.Created);
        await team.Engineer.PutAsJsonAsync($"/api/v1/flights/{other.Flight.Id}/tags", new { tags = AcceptanceTag }, Ct);

        var filtered = await (await team.Viewer.GetAsync($"/api/v1/flights?teamId={team.TeamId}&tag=GPS%20test", Ct)).ReadAsync<Paged<TaggedFlight>>(HttpStatusCode.OK);
        filtered.Items.Select(f => f.Id).Should().Equal(team.FlightId);
        filtered.Items[0].Tags.Should().Contain("GPS test");
        var counts = await (await team.Viewer.GetAsync($"/api/v1/teams/{team.TeamId}/flight-tags", Ct)).ReadAsync<TagCount[]>(HttpStatusCode.OK);
        counts.Should().Equal(new TagCount("acceptance", 2), new TagCount("GPS test", 1));
        (await team.Outsider.GetAsync($"/api/v1/teams/{team.TeamId}/flight-tags", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var activity = await (await team.Viewer.GetAsync($"/api/v1/flights/{team.FlightId}/activity?action=flight.tags_changed", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        activity.Items.Single().Data.Should().Contain("to", "acceptance, GPS test");
    }

    [Fact]
    public async Task The_audit_trail_records_who_did_what_and_only_managers_read_it()
    {
        var team = await CreateTeamAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/flights/{team.FlightId}/comments") { Content = JsonContent.Create(new { body = "traced" }) };
        request.Headers.Add("X-Correlation-Id", "trace-collab-1");
        (await team.Engineer.SendAsync(request, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        var log = await (await team.Owner.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log?pageSize=100", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        log.Items.Select(a => a.Action).Should().Contain(["project.created", "team.member_added", "log_file.uploaded", "comment.created"]);
        log.Items.Should().BeInDescendingOrder(a => a.CreatedAt);
        var traced = log.Items.Single(a => a.Action == "comment.created");
        traced.CorrelationId.Should().Be("trace-collab-1");
        traced.Actor.Id.Should().Be(team.EngineerId);
        log.Items.Where(a => a.Action == "team.member_added").Select(a => a.Data["role"]).Should().BeEquivalentTo(["Engineer", "Viewer"]);

        var members = await (await team.Owner.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log?action=team.", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        members.Items.Should().OnlyContain(a => a.Action.StartsWith("team.", StringComparison.Ordinal)).And.HaveCount(2);
        var byEngineer = await (await team.Owner.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log?actorId={team.EngineerId}", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        byEngineer.Items.Should().OnlyContain(a => a.Actor.Id == team.EngineerId);

        (await team.Engineer.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Viewer.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Outsider.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await team.Outsider.GetAsync($"/api/v1/flights/{team.FlightId}/activity", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await team.Owner.GetAsync("/api/v1/admin/audit-log", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = await factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var all = await (await admin.GetAsync($"/api/v1/admin/audit-log?flightId={team.FlightId}", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        all.Items.Should().Contain(a => a.CorrelationId == "trace-collab-1");

        // The trail outlives the flight it describes.
        (await team.Owner.DeleteAsync($"/api/v1/flights/{team.FlightId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterDelete = await (await team.Owner.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log?action=flight.deleted", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        afterDelete.Items.Single().EntityId.Should().Be(team.FlightId);
        var comments = await (await team.Owner.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log?flightId={team.FlightId}&action=comment.created", Ct)).ReadAsync<Paged<AuditEntry>>(HttpStatusCode.OK);
        comments.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_new_endpoint_follows_the_role_matrix()
    {
        var team = await CreateTeamAsync();
        var admin = await factory.CreateAuthenticatedClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var finding = (await FindingsAsync(team.Owner, team.RunId))[0];
        var comment = await (await team.Engineer.PostAsJsonAsync($"/api/v1/flights/{team.FlightId}/comments", new { body = "matrix" }, Ct)).ReadAsync<Comment>(HttpStatusCode.Created);
        var f = team.FlightId;

        // Reads: every member and administrators; outsiders cannot tell the resource exists.
        string[] reads =
        [
            $"/api/v1/flights/{f}/comments", $"/api/v1/flights/{f}/activity", $"/api/v1/findings/{finding.Id}/history",
            $"/api/v1/teams/{team.TeamId}/finding-reviews", $"/api/v1/teams/{team.TeamId}/flight-tags",
            $"/api/v1/flights/{f}/export/report.json", $"/api/v1/flights/{f}/export/findings.csv", $"/api/v1/flights/{f}/export/events.csv",
        ];
        foreach (var url in reads)
        {
            foreach (var (client, expected) in new[] { (team.Viewer, HttpStatusCode.OK), (team.Engineer, HttpStatusCode.OK), (team.Owner, HttpStatusCode.OK), (admin, HttpStatusCode.OK), (team.Outsider, HttpStatusCode.NotFound) })
            {
                (await client.GetAsync(url, Ct)).StatusCode.Should().Be(expected, url);
            }
        }

        // Writes: engineers and above.
        foreach (var (client, expected) in new[] { (team.Viewer, HttpStatusCode.Forbidden), (team.Outsider, HttpStatusCode.NotFound), (team.Engineer, HttpStatusCode.OK), (team.Owner, HttpStatusCode.OK), (admin, HttpStatusCode.OK) })
        {
            (await client.PutAsJsonAsync($"/api/v1/flights/{f}/tags", new { tags = OneTag }, Ct)).StatusCode.Should().Be(expected, "tags");
            (await client.PatchAsJsonAsync($"/api/v1/findings/{finding.Id}/review", new { flagged = expected == HttpStatusCode.OK }, Ct)).StatusCode.Should().Be(expected, "review");
            (await client.PostAsJsonAsync($"/api/v1/flights/{f}/comments", new { body = "x" }, Ct)).StatusCode
                .Should().Be(expected == HttpStatusCode.OK ? HttpStatusCode.Created : expected, "comment");
        }

        // Someone else's comment: only its author edits it; managers (and administrators) may delete it.
        (await team.Owner.PutAsJsonAsync($"/api/v1/comments/{comment.Id}", new { body = "edited", version = comment.Version }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Viewer.DeleteAsync($"/api/v1/comments/{comment.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await team.Outsider.DeleteAsync($"/api/v1/comments/{comment.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync($"/api/v1/comments/{comment.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The team audit log: managers and administrators only.
        foreach (var (client, expected) in new[] { (team.Viewer, HttpStatusCode.Forbidden), (team.Engineer, HttpStatusCode.Forbidden), (team.Outsider, HttpStatusCode.NotFound), (team.Owner, HttpStatusCode.OK), (admin, HttpStatusCode.OK) })
        {
            (await client.GetAsync($"/api/v1/teams/{team.TeamId}/audit-log", Ct)).StatusCode.Should().Be(expected, "audit log");
        }

        (await team.Viewer.GetAsync("/api/v1/admin/audit-log", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/v1/admin/audit-log", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Audit_entries_past_the_retention_period_are_purged()
    {
        var team = await CreateTeamAsync();
        var oldId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
            db.AuditEntries.Add(new Domain.Collaboration.AuditEntry
            {
                Id = oldId,
                TeamId = team.TeamId,
                ActorId = team.EngineerId,
                Action = "comment.created",
                EntityType = "Comment",
                EntityId = Guid.NewGuid(),
                CorrelationId = "old",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-731),
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IAuditRetention>().PurgeAsync(Ct)).Should().BeGreaterThanOrEqualTo(1);
            var db = scope.ServiceProvider.GetRequiredService<FlightLogDbContext>();
            (await db.AuditEntries.AnyAsync(a => a.Id == oldId, Ct)).Should().BeFalse();
            (await db.AuditEntries.CountAsync(a => a.TeamId == team.TeamId, Ct)).Should().BeGreaterThan(0, "recent entries stay");
        }
    }

    private static readonly string[] MixedTags = ["acceptance", "GPS test", " Acceptance "];
    private static readonly string[] OneTag = ["x"];
    private static readonly string[] TooLongTag = [new string('x', 41)];
    private static readonly string[] AcceptanceTag = ["acceptance"];

    private static async Task<Finding2[]> FindingsAsync(HttpClient client, Guid runId, string? query = null) =>
        (await (await client.GetAsync($"/api/v1/analysis-runs/{runId}/findings?pageSize=100" + (query is null ? "" : "&" + query), Ct))
            .ReadAsync<Paged<Finding2>>(HttpStatusCode.OK)).Items;

    /// <summary>A team with a project manager (owner), an engineer, a viewer, an outsider and an analyzed ArduPilot flight.</summary>
    private async Task<CollaborationTeam> CreateTeamAsync()
    {
        var engineerEmail = $"eng-{Guid.NewGuid():N}@flightlog.test";
        var viewerEmail = $"view-{Guid.NewGuid():N}@flightlog.test";
        var owner = await factory.CreateUserClientAsync(displayName: "Pam Manager");
        var engineer = await factory.CreateUserClientAsync(engineerEmail, "Engin Engineer");
        var viewer = await factory.CreateUserClientAsync(viewerEmail, "Vera Viewer");
        var outsider = await factory.CreateUserClientAsync(displayName: "Otto Outsider");
        var (team, project) = await owner.CreateTeamAndProjectAsync();
        await owner.AddMemberAsync(team.Id, engineerEmail, "Engineer");
        await owner.AddMemberAsync(team.Id, viewerEmail, "Viewer");

        var upload = await (await engineer.UploadLogAsync(project.Id, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ardupilot", "sitl-copter-4.6.3.bin")), "sitl.bin"))
            .ReadAsync<UploadResult>(HttpStatusCode.Created);
        await factory.ProcessQueueAsync();
        var run = (await (await owner.GetAsync($"/api/v1/flights/{upload.Flight.Id}/analysis-runs", Ct)).ReadAsync<AnalysisRun[]>(HttpStatusCode.OK)).Single();
        run.Status.Should().Be("Succeeded");

        return new CollaborationTeam(
            team.Id, project.Id, upload.Flight.Id, run.Id, owner, engineer, viewer, outsider,
            await engineer.CurrentUserIdAsync(), await viewer.CurrentUserIdAsync(), await outsider.CurrentUserIdAsync());
    }
}

internal sealed record CollaborationTeam(
    Guid TeamId, Guid ProjectId, Guid FlightId, Guid RunId, HttpClient Owner, HttpClient Engineer, HttpClient Viewer, HttpClient Outsider,
    Guid EngineerId, Guid ViewerId, Guid OutsiderId);

internal sealed record UserRef(Guid Id, string DisplayName);

internal sealed record Comment(
    Guid Id, Guid FlightId, Guid? FindingReviewId, UserRef Author, string? Body, long? StartUs, long? EndUs, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt,
    bool Deleted, bool CanEdit, bool CanDelete, uint Version);

internal sealed record Review(Guid Id, string Status, UserRef? Assignee, bool Flagged, UserRef UpdatedBy, DateTimeOffset UpdatedAt, int CommentCount);

internal sealed record Finding2(Guid Id, string RuleId, string Title, long StartUs, long EndUs, string Severity, Review? Review);

internal sealed record AuditEntry(
    Guid Id, string Action, string EntityType, Guid EntityId, Guid? FlightId, UserRef Actor, string? Summary, Dictionary<string, string?> Data, string CorrelationId,
    DateTimeOffset CreatedAt);

internal sealed record ReviewItem(
    Guid ReviewId, Guid FlightId, string FlightName, string RuleId, long StartUs, long EndUs, string Status, UserRef? Assignee, bool Flagged, Guid? FindingId,
    string? Title, string? Severity);

internal sealed record TaggedFlight(Guid Id, string Name, string[] Tags);

internal sealed record TagCount(string Tag, int Flights);
