namespace VerityWorkbench.Media.Tests;

public sealed class ProcessingJobDirectoryServiceTests
{
    [Fact]
    public void InspectReturnsMissingWithoutDisclosingAFullPath()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var relativePath = CanonicalRelativePath(jobId);

        var inspection = new ProcessingJobDirectoryService().Inspect(
            workspace.Layout,
            jobId,
            relativePath);

        Assert.Equal(ProcessingJobDirectoryState.Missing, inspection.State);
        Assert.False(inspection.IsPresent);
        Assert.Null(inspection.FullPath);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheExactJobAndPreservesItsSibling()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        var job = CreateJob(workspace, jobId);
        var sibling = CreateJob(workspace, siblingId);
        var nestedDirectory = Path.Combine(job.FullPath, "items", "one");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllBytesAsync(Path.Combine(nestedDirectory, "payload.bin"), [1, 2, 3]);
        var siblingSentinel = Path.Combine(sibling.FullPath, "keep.bin");
        await File.WriteAllBytesAsync(siblingSentinel, [4, 5, 6]);

        var service = new ProcessingJobDirectoryService();
        var inspection = service.Inspect(workspace.Layout, jobId, job.RelativePath);
        Assert.Equal(ProcessingJobDirectoryState.Present, inspection.State);
        Assert.Equal(job.FullPath, inspection.FullPath);

        await service.DeleteAsync(workspace.Layout, jobId, job.RelativePath);

        Assert.False(Directory.Exists(job.FullPath));
        Assert.True(Directory.Exists(sibling.FullPath));
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(siblingSentinel));
    }

    [Theory]
    [InlineData("Processing")]
    [InlineData("Processing/../outside")]
    [InlineData("Processing/child/extra")]
    [InlineData("processing/child")]
    [InlineData("Processing\\child")]
    public void InspectRejectsNonCanonicalOrNonDirectChildPaths(string storedPath)
    {
        using var workspace = new TestWorkspace();

        var exception = Assert.Throws<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().Inspect(
                workspace.Layout,
                Guid.NewGuid(),
                storedPath));

        Assert.Equal(ProcessingJobDirectoryFailure.PathInvalid, exception.Failure);
    }

    [Fact]
    public void InspectRejectsAnAbsoluteOutsidePath()
    {
        using var workspace = new TestWorkspace();

        var exception = Assert.Throws<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().Inspect(
                workspace.Layout,
                Guid.NewGuid(),
                workspace.Sources));

        Assert.Equal(ProcessingJobDirectoryFailure.PathInvalid, exception.Failure);
    }

    [Fact]
    public void InspectRejectsAJobIdThatDoesNotMatchTheDirectorySuffix()
    {
        using var workspace = new TestWorkspace();
        var storedJob = CreateJob(workspace, Guid.NewGuid());

        var exception = Assert.Throws<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().Inspect(
                workspace.Layout,
                Guid.NewGuid(),
                storedJob.RelativePath));

        Assert.Equal(ProcessingJobDirectoryFailure.JobIdMismatch, exception.Failure);
    }

    [Fact]
    public void InspectRejectsAJobWithoutARegularMarker()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var relativePath = CanonicalRelativePath(jobId);
        Directory.CreateDirectory(FullPath(workspace, relativePath));

        var exception = Assert.Throws<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().Inspect(
                workspace.Layout,
                jobId,
                relativePath));

        Assert.Equal(ProcessingJobDirectoryFailure.MarkerInvalid, exception.Failure);
    }

    [Fact]
    public void InspectRejectsAFileAtTheStoredTarget()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var relativePath = CanonicalRelativePath(jobId);
        File.WriteAllBytes(FullPath(workspace, relativePath), [1]);

        var exception = Assert.Throws<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().Inspect(
                workspace.Layout,
                jobId,
                relativePath));

        Assert.Equal(ProcessingJobDirectoryFailure.TargetNotDirectory, exception.Failure);
    }

    [Theory]
    [InlineData(".promotion-journal")]
    [InlineData(".preprocessing-promotion-journal")]
    public async Task InspectAndDeleteRefuseNonEmptyPromotionEvidence(string journalName)
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var job = CreateJob(workspace, jobId);
        var journal = Path.Combine(job.FullPath, journalName);
        Directory.CreateDirectory(journal);
        await File.WriteAllTextAsync(Path.Combine(journal, "pending.json"), "{}");
        var service = new ProcessingJobDirectoryService();

        var inspectException = Assert.Throws<ProcessingJobDirectoryException>(() =>
            service.Inspect(workspace.Layout, jobId, job.RelativePath));
        var deleteException = await Assert.ThrowsAsync<ProcessingJobDirectoryException>(() =>
            service.DeleteAsync(workspace.Layout, jobId, job.RelativePath));

        Assert.Equal(
            ProcessingJobDirectoryFailure.PendingPromotionEvidence,
            inspectException.Failure);
        Assert.Equal(
            ProcessingJobDirectoryFailure.PendingPromotionEvidence,
            deleteException.Failure);
        Assert.True(Directory.Exists(job.FullPath));
        Assert.True(File.Exists(Path.Combine(journal, "pending.json")));
    }

    [Fact]
    public async Task DeleteAllowsAnEmptyPromotionJournal()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var job = CreateJob(workspace, jobId);
        Directory.CreateDirectory(Path.Combine(job.FullPath, ".promotion-journal"));

        await new ProcessingJobDirectoryService().DeleteAsync(
            workspace.Layout,
            jobId,
            job.RelativePath);

        Assert.False(Directory.Exists(job.FullPath));
    }

    [Fact]
    public async Task DeleteRefusesMissingInsteadOfTreatingItAsSuccess()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().DeleteAsync(
                workspace.Layout,
                jobId,
                CanonicalRelativePath(jobId)));

        Assert.Equal(ProcessingJobDirectoryFailure.Missing, exception.Failure);
    }

    [Fact]
    public async Task CancellationBeforeWorkerMutationPreservesTheJob()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var job = CreateJob(workspace, jobId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessingJobDirectoryService().DeleteAsync(
                workspace.Layout,
                jobId,
                job.RelativePath,
                cancellation.Token));

        Assert.True(Directory.Exists(job.FullPath));
        Assert.True(File.Exists(Path.Combine(job.FullPath, ".job")));
    }

    [Fact]
    public async Task LockedFileFailureIsRetryableAndLeavesTheJobMarkerUntilSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var job = CreateJob(workspace, jobId);
        var markerPath = Path.Combine(job.FullPath, ".job");
        var temporaryMarkerPath = Path.Combine(job.FullPath, ".marker-case-change");
        var uppercaseMarkerPath = Path.Combine(job.FullPath, ".JOB");
        File.Move(markerPath, temporaryMarkerPath);
        File.Move(temporaryMarkerPath, uppercaseMarkerPath);
        var lockedPath = Path.Combine(job.FullPath, "locked.bin");
        await File.WriteAllBytesAsync(lockedPath, [1, 2, 3]);
        var service = new ProcessingJobDirectoryService();

        Exception failure;
        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.DeleteAsync(workspace.Layout, jobId, job.RelativePath));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.True(Directory.Exists(job.FullPath));
            Assert.True(File.Exists(uppercaseMarkerPath));
        }

        await service.DeleteAsync(workspace.Layout, jobId, job.RelativePath);
        Assert.False(Directory.Exists(job.FullPath));
    }

    [Fact]
    public void WindowsDirectoryLeasesPreventBoundaryRenames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var job = CreateJob(workspace, Guid.NewGuid());
        var boundaries = new[]
        {
            workspace.Layout.WorkspaceRoot,
            workspace.Layout.ProcessingRoot,
            job.FullPath,
        };

        foreach (var boundary in boundaries)
        {
            using var lease =
                ProcessingJobDirectoryService.OpenWindowsDirectoryLease(boundary);
            var movedPath = boundary + ".moved";

            var exception = Record.Exception(() =>
                Directory.Move(boundary, movedPath));

            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                exception?.GetType().FullName ?? "The rename unexpectedly succeeded.");
            Assert.True(Directory.Exists(boundary));
            Assert.False(Directory.Exists(movedPath));
        }
    }

    [Fact]
    public void InspectRejectsAReparsePointAtTheProcessingRoot()
    {
        using var workspace = new TestWorkspace();
        var external = Path.Combine(workspace.Sources, "processing-target");
        Directory.CreateDirectory(external);
        Directory.Delete(workspace.Layout.ProcessingRoot);
        if (!TryCreateDirectoryLink(workspace.Layout.ProcessingRoot, external))
        {
            Directory.CreateDirectory(workspace.Layout.ProcessingRoot);
            return;
        }

        var jobId = Guid.NewGuid();
        var exception = Assert.Throws<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().Inspect(
                workspace.Layout,
                jobId,
                CanonicalRelativePath(jobId)));

        Assert.Equal(ProcessingJobDirectoryFailure.ReparsePointDetected, exception.Failure);
    }

    [Fact]
    public void InspectRejectsAReparsePointAsTheJobTarget()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var relativePath = CanonicalRelativePath(jobId);
        var external = Path.Combine(workspace.Sources, "job-target");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, ".job"), []);
        if (!TryCreateDirectoryLink(FullPath(workspace, relativePath), external))
        {
            return;
        }

        var exception = Assert.Throws<ProcessingJobDirectoryException>(() =>
            new ProcessingJobDirectoryService().Inspect(
                workspace.Layout,
                jobId,
                relativePath));

        Assert.Equal(ProcessingJobDirectoryFailure.ReparsePointDetected, exception.Failure);
    }

    [Fact]
    public async Task InspectAndDeleteRejectANestedReparsePointWithoutTouchingItsTarget()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var job = CreateJob(workspace, jobId);
        var external = Path.Combine(workspace.Sources, "outside-tree");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.bin");
        await File.WriteAllBytesAsync(sentinel, [7, 8, 9]);
        var link = Path.Combine(job.FullPath, "nested-link");
        if (!TryCreateDirectoryLink(link, external))
        {
            return;
        }

        var service = new ProcessingJobDirectoryService();
        var inspectException = Assert.Throws<ProcessingJobDirectoryException>(() =>
            service.Inspect(workspace.Layout, jobId, job.RelativePath));
        var deleteException = await Assert.ThrowsAsync<ProcessingJobDirectoryException>(() =>
            service.DeleteAsync(workspace.Layout, jobId, job.RelativePath));

        Assert.Equal(
            ProcessingJobDirectoryFailure.ReparsePointDetected,
            inspectException.Failure);
        Assert.Equal(
            ProcessingJobDirectoryFailure.ReparsePointDetected,
            deleteException.Failure);
        Assert.Equal([7, 8, 9], await File.ReadAllBytesAsync(sentinel));
        Assert.True(Directory.Exists(job.FullPath));
    }

    private static JobPath CreateJob(TestWorkspace workspace, Guid jobId)
    {
        var relativePath = CanonicalRelativePath(jobId);
        var fullPath = FullPath(workspace, relativePath);
        Directory.CreateDirectory(fullPath);
        File.WriteAllBytes(Path.Combine(fullPath, ".job"), []);
        return new(relativePath, fullPath);
    }

    private static string CanonicalRelativePath(Guid jobId) =>
        $"Processing/20260817T1200000000000Z_cleanup_{jobId.ToString("N")[..12]}";

    private static string FullPath(TestWorkspace workspace, string relativePath) =>
        Path.Combine(
            workspace.Layout.WorkspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record JobPath(string RelativePath, string FullPath);
}
