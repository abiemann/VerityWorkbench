using System.Globalization;
using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.Data.Profiles;

public sealed class SqliteProfileStore
{
    private const int SchemaVersion = 4;
    private const int MaximumStoredErrorLength = 2_048;
    private const int MaximumProbeTextLength = 4_096;
    private const string MediaIntegrityFailureReason =
        "Registered media failed integrity verification; exclude or repair it before validation.";

    private const string CreateVersion1SchemaSql = """
        CREATE TABLE IF NOT EXISTS profiles (
            id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            workspace_root TEXT NOT NULL,
            download_staging_root TEXT NULL,
            readiness TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS training_videos (
            id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NOT NULL,
            file_path TEXT NOT NULL,
            recording_date_label TEXT NOT NULL,
            training_condition TEXT NOT NULL,
            is_archived INTEGER NOT NULL CHECK (is_archived IN (0, 1)),
            sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_training_videos_profile_order
            ON training_videos(profile_id, sort_order, id);

        PRAGMA user_version = 1;
        """;

    private const string MigrateVersion1ToVersion2Sql = """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_profiles_workspace_root_nocase
            ON profiles(workspace_root COLLATE NOCASE);

        PRAGMA user_version = 2;
        """;

    private const string MigrateVersion2ToVersion3Sql = """
        CREATE TABLE media_assets (
            id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            workspace_relative_path TEXT NOT NULL,
            byte_length INTEGER NOT NULL CHECK (byte_length >= 0),
            state TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
            UNIQUE (profile_id, sha256)
        );

        CREATE INDEX ix_media_assets_profile
            ON media_assets(profile_id, created_utc, id);

        CREATE UNIQUE INDEX ux_media_assets_profile_path_nocase
            ON media_assets(profile_id, workspace_relative_path COLLATE NOCASE);

        CREATE TABLE processing_jobs (
            id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            state TEXT NOT NULL,
            completed_item_count INTEGER NOT NULL CHECK (completed_item_count >= 0),
            total_item_count INTEGER NOT NULL CHECK (total_item_count >= 0),
            completed_bytes INTEGER NOT NULL CHECK (completed_bytes >= 0),
            total_bytes INTEGER NOT NULL CHECK (total_bytes >= 0),
            workspace_relative_path TEXT NOT NULL,
            error TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
            CHECK (completed_item_count <= total_item_count),
            CHECK (completed_bytes <= total_bytes)
        );

        CREATE INDEX ix_processing_jobs_profile_created
            ON processing_jobs(profile_id, created_utc, id);

        CREATE UNIQUE INDEX ux_processing_jobs_one_active_per_profile
            ON processing_jobs(profile_id)
            WHERE state IN ('Queued', 'Running');

        ALTER TABLE training_videos
            ADD COLUMN media_asset_id TEXT NULL REFERENCES media_assets(id) ON DELETE SET NULL;

        CREATE INDEX ix_training_videos_media_asset
            ON training_videos(media_asset_id);

        PRAGMA user_version = 3;
        """;

    private const string MigrateVersion3ToVersion4Sql = """
        ALTER TABLE media_assets
            ADD COLUMN probe_failure TEXT NULL CHECK (
                probe_failure IS NULL OR length(probe_failure) <= 2048);

        CREATE TABLE media_validation_results (
            media_asset_id TEXT NOT NULL PRIMARY KEY,
            container_format TEXT NOT NULL CHECK (length(container_format) BETWEEN 1 AND 4096),
            container_major_brand TEXT NULL CHECK (
                container_major_brand IS NULL OR length(container_major_brand) BETWEEN 1 AND 4096),
            video_stream_index INTEGER NOT NULL CHECK (video_stream_index >= 0),
            video_codec TEXT NOT NULL CHECK (length(video_codec) BETWEEN 1 AND 4096),
            width INTEGER NOT NULL CHECK (width > 0),
            height INTEGER NOT NULL CHECK (height > 0),
            audio_stream_index INTEGER NOT NULL CHECK (audio_stream_index >= 0),
            audio_codec TEXT NOT NULL CHECK (length(audio_codec) BETWEEN 1 AND 4096),
            audio_sample_rate_hz INTEGER NOT NULL CHECK (audio_sample_rate_hz > 0),
            audio_channel_count INTEGER NOT NULL CHECK (audio_channel_count > 0),
            duration_microseconds INTEGER NOT NULL CHECK (duration_microseconds > 0),
            frame_rate_numerator INTEGER NOT NULL CHECK (frame_rate_numerator > 0),
            frame_rate_denominator INTEGER NOT NULL CHECK (frame_rate_denominator > 0),
            ffprobe_version TEXT NOT NULL CHECK (length(ffprobe_version) BETWEEN 1 AND 4096),
            ffprobe_compiler_identifier TEXT NOT NULL CHECK (
                length(ffprobe_compiler_identifier) BETWEEN 1 AND 4096),
            ffprobe_configuration TEXT NOT NULL CHECK (
                length(ffprobe_configuration) BETWEEN 1 AND 4096),
            ffprobe_configuration_sha256 TEXT NOT NULL CHECK (
                length(ffprobe_configuration_sha256) = 64),
            ffprobe_executable_sha256 TEXT NOT NULL CHECK (length(ffprobe_executable_sha256) = 64),
            ffmpeg_version TEXT NOT NULL CHECK (length(ffmpeg_version) BETWEEN 1 AND 4096),
            ffmpeg_compiler_identifier TEXT NOT NULL CHECK (
                length(ffmpeg_compiler_identifier) BETWEEN 1 AND 4096),
            ffmpeg_configuration TEXT NOT NULL CHECK (
                length(ffmpeg_configuration) BETWEEN 1 AND 4096),
            ffmpeg_configuration_sha256 TEXT NOT NULL CHECK (
                length(ffmpeg_configuration_sha256) = 64),
            ffmpeg_executable_sha256 TEXT NOT NULL CHECK (length(ffmpeg_executable_sha256) = 64),
            validation_contract_sha256 TEXT NOT NULL CHECK (length(validation_contract_sha256) = 64),
            decode_completed INTEGER NOT NULL CHECK (decode_completed = 1),
            decoded_duration_microseconds INTEGER NOT NULL CHECK (decoded_duration_microseconds > 0),
            validated_utc TEXT NOT NULL,
            FOREIGN KEY (media_asset_id) REFERENCES media_assets(id) ON DELETE CASCADE
        );

        CREATE TRIGGER media_validation_results_immutable_update
        BEFORE UPDATE ON media_validation_results
        BEGIN
            SELECT RAISE(ABORT, 'Successful media probe results are immutable.');
        END;

        CREATE TABLE media_validation_job_assets (
            job_id TEXT NOT NULL,
            media_asset_id TEXT NOT NULL,
            PRIMARY KEY (job_id, media_asset_id),
            FOREIGN KEY (job_id) REFERENCES processing_jobs(id) ON DELETE CASCADE,
            FOREIGN KEY (media_asset_id) REFERENCES media_assets(id) ON DELETE CASCADE
        );

        CREATE INDEX ix_media_validation_job_assets_asset
            ON media_validation_job_assets(media_asset_id, job_id);

        PRAGMA user_version = 4;
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public SqliteProfileStore(string databasePath, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var parentDirectory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            using var transaction = connection.BeginTransaction(deferred: false);
            var version = await ReadSchemaVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The profile database schema version {version} is newer than supported version {SchemaVersion}.");
            }

            if (version < 1)
            {
                await ApplyMigrationStepAsync(
                        connection,
                        transaction,
                        CreateVersion1SchemaSql,
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                await ApplyMigrationStepAsync(
                        connection,
                        transaction,
                        MigrateVersion1ToVersion2Sql,
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 2;
            }

            if (version < 3)
            {
                await ApplyMigrationStepAsync(
                        connection,
                        transaction,
                        MigrateVersion2ToVersion3Sql,
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 3;
            }

            if (version < 4)
            {
                await ApplyMigrationStepAsync(
                        connection,
                        transaction,
                        MigrateVersion3ToVersion4Sql,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task AddAsync(
        StoredProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        try
        {
            await EnsureIdentityAndLocationsAllowedAsync(
                    connection,
                    transaction,
                    profile,
                    isUpdate: false,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertProfileAsync(connection, transaction, profile, cancellationToken)
                .ConfigureAwait(false);
            await InsertTrainingVideosAsync(connection, transaction, profile, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsDisplayNameConflict(exception))
        {
            throw new ProfileNameConflictException(profile.DisplayName, exception);
        }
        catch (SqliteException exception) when (IsWorkspaceRootConflict(exception))
        {
            throw new ProfileWorkspaceConflictException(profile.WorkspaceRoot, exception);
        }
    }

    public async Task UpdateAsync(
        StoredProfile profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            if (await HasActiveJobAsync(connection, transaction, profile.Id, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new ProfileProcessingActiveException(profile.Id);
            }

            await EnsureIdentityAndLocationsAllowedAsync(
                    connection,
                    transaction,
                    profile,
                    isUpdate: true,
                    cancellationToken)
                .ConfigureAwait(false);
            var affectedRows = await UpdateProfileRowAsync(
                    connection,
                    transaction,
                    profile,
                    expectedUpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if (affectedRows == 0)
            {
                throw new ProfileConcurrencyConflictException(profile.Id, expectedUpdatedAtUtc);
            }

            await DeleteTrainingVideosAsync(connection, transaction, profile.Id, cancellationToken)
                .ConfigureAwait(false);
            await InsertTrainingVideosAsync(connection, transaction, profile, cancellationToken)
                .ConfigureAwait(false);
            if (await HasAnyMediaAssetLinksAsync(
                    connection,
                    transaction,
                    profile.Id,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                var readiness = await DetermineMediaReadinessAsync(
                        connection,
                        transaction,
                        profile.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SetProfileReadinessAsync(
                        connection,
                        transaction,
                        profile.Id,
                        readiness,
                        profile.UpdatedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsDisplayNameConflict(exception))
        {
            throw new ProfileNameConflictException(profile.DisplayName, exception);
        }
        catch (SqliteException exception) when (IsWorkspaceRootConflict(exception))
        {
            throw new ProfileWorkspaceConflictException(profile.WorkspaceRoot, exception);
        }
    }

    public async Task<StoredProfile?> GetByIdAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(profileId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var profile = await ReadProfileAsync(connection, profileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        var videos = await ReadTrainingVideosAsync(connection, profile.Id, cancellationToken)
            .ConfigureAwait(false);
        return profile with { TrainingVideos = videos };
    }

    public async Task<IReadOnlyList<StoredProfile>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var profiles = await ReadProfilesAsync(connection, cancellationToken).ConfigureAwait(false);
        var results = new List<StoredProfile>(profiles.Count);

        foreach (var profile in profiles)
        {
            var videos = await ReadTrainingVideosAsync(connection, profile.Id, cancellationToken)
                .ConfigureAwait(false);
            results.Add(profile with { TrainingVideos = videos });
        }

        return results;
    }

    public async Task<StoredProcessingJob> StartLocalMediaIngestJobAsync(
        Guid profileId,
        DateTimeOffset expectedUpdatedAtUtc,
        Guid jobId,
        string workspaceRelativePath,
        int totalItemCount,
        long totalBytes,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(profileId, nameof(profileId));
        ValidateRequiredId(jobId, nameof(jobId));
        var normalizedJobPath = NormalizeBoundedWorkspaceRelativePath(
            workspaceRelativePath,
            "Processing",
            nameof(workspaceRelativePath));
        ValidateProgress(0, totalItemCount, 0, totalBytes);
        if (totalItemCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalItemCount),
                "A local-ingest job must contain at least one item.");
        }

        if (startedAtUtc <= expectedUpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startedAtUtc),
                "The job timestamp must be later than the expected profile timestamp.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        if (await HasActiveJobAsync(connection, transaction, profileId, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new ProfileProcessingActiveException(profileId);
        }

        var timestamp = FormatTimestamp(startedAtUtc);
        await using (var updateProfile = connection.CreateCommand())
        {
            updateProfile.Transaction = transaction;
            updateProfile.CommandText = """
                UPDATE profiles
                SET readiness = $readiness,
                    updated_utc = $updatedUtc
                WHERE id = $profileId
                  AND updated_utc = $expectedUpdatedUtc;
                """;
            updateProfile.Parameters.AddWithValue("$readiness", ProfileReadiness.IngestingMedia.ToString());
            updateProfile.Parameters.AddWithValue("$updatedUtc", timestamp);
            updateProfile.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            updateProfile.Parameters.AddWithValue(
                "$expectedUpdatedUtc",
                FormatTimestamp(expectedUpdatedAtUtc));
            var affected = await updateProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                if (!await ProfileExistsAsync(connection, transaction, profileId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Profile '{profileId}' was not found.");
                }

                throw new ProfileConcurrencyConflictException(profileId, expectedUpdatedAtUtc);
            }
        }

        var job = new StoredProcessingJob(
            jobId,
            profileId,
            ProcessingJobKind.LocalMediaIngest,
            ProcessingJobState.Queued,
            0,
            totalItemCount,
            0,
            totalBytes,
            normalizedJobPath,
            null,
            startedAtUtc.ToUniversalTime(),
            startedAtUtc.ToUniversalTime());

        await InsertProcessingJobAsync(connection, transaction, job, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<bool> UpdateProcessingJobProgressAsync(
        Guid jobId,
        ProcessingJobState state,
        int completedItemCount,
        long completedBytes,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(jobId, nameof(jobId));
        if (state is not ProcessingJobState.Queued and not ProcessingJobState.Running)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Progress can only be reported for a queued or running job.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = await ReadProcessingJobAsync(connection, transaction, jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Processing job '{jobId}' was not found.");
        ValidateProgress(completedItemCount, job.TotalItemCount, completedBytes, job.TotalBytes);
        ValidateTimestampNotBefore(updatedAtUtc, job.UpdatedAtUtc, nameof(updatedAtUtc));

        if (job.State is not ProcessingJobState.Queued and not ProcessingJobState.Running)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (job.State == ProcessingJobState.Running && state == ProcessingJobState.Queued)
        {
            throw new InvalidOperationException("A running processing job cannot return to the queued state.");
        }

        if (completedItemCount < job.CompletedItemCount || completedBytes < job.CompletedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedItemCount),
                "Processing progress cannot move backwards.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processing_jobs
            SET state = $state,
                completed_item_count = $completedItemCount,
                completed_bytes = $completedBytes,
                updated_utc = $updatedUtc
            WHERE id = $id
              AND state IN ('Queued', 'Running');
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$completedItemCount", completedItemCount);
        command.Parameters.AddWithValue("$completedBytes", completedBytes);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(updatedAtUtc));
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<IReadOnlyList<StoredMediaAsset>> CompleteLocalMediaIngestJobAsync(
        Guid jobId,
        IReadOnlyList<MediaAssetRegistration> registrations,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(jobId, nameof(jobId));
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
        {
            ValidateRegistration(registration);
        }

        var duplicateVideoId = registrations
            .GroupBy(registration => registration.TrainingVideoId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateVideoId is not null)
        {
            throw new ArgumentException(
                $"Training video ID '{duplicateVideoId.Key}' appears more than once.",
                nameof(registrations));
        }

        var reusedCandidateId = registrations
            .GroupBy(registration => registration.MediaAssetId)
            .FirstOrDefault(group => group.Select(registration => registration.Sha256).Distinct().Count() > 1);
        if (reusedCandidateId is not null)
        {
            throw new ArgumentException(
                $"Media asset ID '{reusedCandidateId.Key}' cannot identify different SHA-256 hashes.",
                nameof(registrations));
        }

        var reusedCandidatePath = registrations
            .GroupBy(
                registration => NormalizeBoundedWorkspaceRelativePath(
                    registration.WorkspaceRelativePath,
                    "Media",
                    nameof(registrations)),
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Select(registration => registration.Sha256).Distinct().Count() > 1);
        if (reusedCandidatePath is not null)
        {
            throw new ArgumentException(
                $"Workspace-relative path '{reusedCandidatePath.Key}' cannot identify different SHA-256 hashes.",
                nameof(registrations));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = await ReadProcessingJobAsync(connection, transaction, jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Processing job '{jobId}' was not found.");
        if (job.Kind != ProcessingJobKind.LocalMediaIngest ||
            job.State is not ProcessingJobState.Queued and not ProcessingJobState.Running)
        {
            throw new InvalidOperationException($"Processing job '{jobId}' is not an active local-ingest job.");
        }
        ValidateTimestampNotBefore(completedAtUtc, job.UpdatedAtUtc, nameof(completedAtUtc));

        if (registrations.Count != job.TotalItemCount)
        {
            throw new ArgumentException(
                "The number of media registrations must equal the job's total item count.",
                nameof(registrations));
        }

        long registeredBytes;
        try
        {
            registeredBytes = registrations.Aggregate(
                0L,
                (total, registration) => checked(total + registration.ByteLength));
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registrations),
                "The registered byte total is too large.");
        }

        if (registeredBytes != job.TotalBytes)
        {
            throw new ArgumentException(
                "The registered byte total must equal the job's total byte count.",
                nameof(registrations));
        }

        var videoConditions = await ReadTrainingVideoConditionsAsync(
                connection,
                transaction,
                job.ProfileId,
                registrations.Select(registration => registration.TrainingVideoId),
                cancellationToken)
            .ConfigureAwait(false);

        var completedAssets = new List<StoredMediaAsset>();
        foreach (var group in registrations.GroupBy(registration => registration.Sha256))
        {
            var first = group.First();
            var firstRelativePath = NormalizeBoundedWorkspaceRelativePath(
                first.WorkspaceRelativePath,
                "Media",
                nameof(registrations));
            if (group.Any(registration =>
                    registration.MediaAssetId != first.MediaAssetId ||
                    registration.ByteLength != first.ByteLength ||
                    !string.Equals(
                        NormalizeBoundedWorkspaceRelativePath(
                            registration.WorkspaceRelativePath,
                            "Media",
                            nameof(registrations)),
                        firstRelativePath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"Registrations for hash '{group.Key}' must use exactly one media asset ID, " +
                    "one canonical workspace-relative path, and one byte length.",
                    nameof(registrations));
            }

            var requestedConditions = group
                .Select(registration => videoConditions[registration.TrainingVideoId])
                .Distinct()
                .ToArray();
            if (requestedConditions.Length > 1)
            {
                throw new MediaAssetConditionConflictException(
                    group.Key,
                    requestedConditions[0],
                    requestedConditions[1]);
            }

            var asset = await ReadMediaAssetByHashAsync(
                    connection,
                    transaction,
                    job.ProfileId,
                    group.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (asset is not null && asset.ByteLength != first.ByteLength)
            {
                throw new InvalidDataException(
                    $"Stored media asset '{asset.Id}' has a byte length inconsistent with its SHA-256 hash.");
            }

            if (asset is not null)
            {
                var linkedConditions = await ReadLinkedConditionsAsync(
                        connection,
                        transaction,
                        asset.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
                var conflictingCondition = linkedConditions
                    .FirstOrDefault(condition => condition != requestedConditions[0]);
                if (linkedConditions.Any(condition => condition != requestedConditions[0]))
                {
                    throw new MediaAssetConditionConflictException(
                        group.Key,
                        conflictingCondition,
                        requestedConditions[0]);
                }
            }
            else
            {
                var pathOwner = await ReadMediaAssetByPathAsync(
                        connection,
                        transaction,
                        job.ProfileId,
                        firstRelativePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (pathOwner is not null)
                {
                    throw new InvalidDataException(
                        $"Workspace-relative media path '{firstRelativePath}' is already registered " +
                        $"for different content in profile '{job.ProfileId}'.");
                }

                var now = completedAtUtc.ToUniversalTime();
                asset = new StoredMediaAsset(
                    first.MediaAssetId,
                    job.ProfileId,
                    first.Sha256,
                    firstRelativePath,
                    first.ByteLength,
                    MediaAssetState.AwaitingProbe,
                    null,
                    now,
                    now);
                await InsertMediaAssetAsync(connection, transaction, asset, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var registration in group)
            {
                await LinkTrainingVideoAsync(
                        connection,
                        transaction,
                        registration.TrainingVideoId,
                        asset.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            completedAssets.Add(asset);
        }

        await SetJobTerminalAsync(
                connection,
                transaction,
                job,
                ProcessingJobState.Completed,
                error: null,
                completedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        var readiness = await DetermineMediaReadinessAsync(
                connection,
                transaction,
                job.ProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        await SetProfileReadinessAsync(
                connection,
                transaction,
                job.ProfileId,
                readiness,
                completedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return completedAssets;
    }

    public async Task<StoredProcessingJob> StartMediaValidationJobAsync(
        Guid profileId,
        DateTimeOffset expectedUpdatedAtUtc,
        Guid jobId,
        string workspaceRelativePath,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(profileId, nameof(profileId));
        ValidateRequiredId(jobId, nameof(jobId));
        var normalizedJobPath = NormalizeBoundedWorkspaceRelativePath(
            workspaceRelativePath,
            "Processing",
            nameof(workspaceRelativePath));
        if (startedAtUtc <= expectedUpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startedAtUtc),
                "The job timestamp must be later than the expected profile timestamp.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await HasActiveJobAsync(connection, transaction, profileId, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new ProfileProcessingActiveException(profileId);
        }

        var activeAssets = await ReadActiveMediaAssetsAsync(
                connection,
                transaction,
                profileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (activeAssets.Any(asset => asset.State == MediaAssetState.IntegrityFailed))
        {
            throw new InvalidOperationException(
                $"Profile '{profileId}' has active media that failed integrity verification.");
        }

        if (await HasUnlinkedActiveTrainingVideosAsync(
                connection,
                transaction,
                profileId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Profile '{profileId}' has active training videos that have not been ingested.");
        }

        var pendingAssets = activeAssets
            .Where(asset => asset.State != MediaAssetState.Validated)
            .ToArray();
        if (pendingAssets.Length == 0)
        {
            throw new InvalidOperationException(
                $"Profile '{profileId}' has no active media assets requiring validation.");
        }

        long totalBytes;
        try
        {
            totalBytes = pendingAssets.Aggregate(0L, (total, asset) => checked(total + asset.ByteLength));
        }
        catch (OverflowException)
        {
            throw new InvalidDataException($"Profile '{profileId}' has an invalid active-media byte total.");
        }

        var timestamp = FormatTimestamp(startedAtUtc);
        await using (var updateProfile = connection.CreateCommand())
        {
            updateProfile.Transaction = transaction;
            updateProfile.CommandText = """
                UPDATE profiles
                SET readiness = $readiness,
                    updated_utc = $updatedUtc
                WHERE id = $profileId
                  AND updated_utc = $expectedUpdatedUtc;
                """;
            updateProfile.Parameters.AddWithValue("$readiness", ProfileReadiness.ValidatingMedia.ToString());
            updateProfile.Parameters.AddWithValue("$updatedUtc", timestamp);
            updateProfile.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            updateProfile.Parameters.AddWithValue(
                "$expectedUpdatedUtc",
                FormatTimestamp(expectedUpdatedAtUtc));
            var affected = await updateProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                if (!await ProfileExistsAsync(connection, transaction, profileId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Profile '{profileId}' was not found.");
                }

                throw new ProfileConcurrencyConflictException(profileId, expectedUpdatedAtUtc);
            }
        }

        var job = new StoredProcessingJob(
            jobId,
            profileId,
            ProcessingJobKind.MediaValidation,
            ProcessingJobState.Queued,
            0,
            pendingAssets.Length,
            0,
            totalBytes,
            normalizedJobPath,
            null,
            startedAtUtc.ToUniversalTime(),
            startedAtUtc.ToUniversalTime());
        await InsertProcessingJobAsync(connection, transaction, job, cancellationToken)
            .ConfigureAwait(false);
        await InsertMediaValidationJobAssetsAsync(
                connection,
                transaction,
                job.Id,
                pendingAssets.Select(asset => asset.Id),
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<IReadOnlyList<StoredMediaAsset>> CompleteMediaValidationJobAsync(
        Guid jobId,
        IReadOnlyList<MediaValidationRegistration> registrations,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(jobId, nameof(jobId));
        ArgumentNullException.ThrowIfNull(registrations);
        var normalizedRegistrations = registrations
            .Select(registration => registration with
            {
                FailureMessage = SanitizeValidationFailure(registration.FailureMessage),
            })
            .ToArray();
        foreach (var registration in normalizedRegistrations)
        {
            ValidateMediaValidationRegistration(registration);
        }

        if (normalizedRegistrations.Select(registration => registration.MediaAssetId).Distinct().Count() !=
            normalizedRegistrations.Length)
        {
            throw new ArgumentException(
                "A media asset may appear only once in a validation batch.",
                nameof(registrations));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = await ReadProcessingJobAsync(connection, transaction, jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Processing job '{jobId}' was not found.");
        if (job.Kind != ProcessingJobKind.MediaValidation ||
            job.State is not ProcessingJobState.Queued and not ProcessingJobState.Running)
        {
            throw new InvalidOperationException(
                $"Processing job '{jobId}' is not an active media-validation job.");
        }

        ValidateTimestampNotBefore(completedAtUtc, job.UpdatedAtUtc, nameof(completedAtUtc));
        var expectedAssetIds = await ReadMediaValidationJobAssetIdsAsync(
                connection,
                transaction,
                job.Id,
                cancellationToken)
            .ConfigureAwait(false);
        var suppliedAssetIds = normalizedRegistrations
            .Select(registration => registration.MediaAssetId)
            .ToHashSet();
        if (expectedAssetIds.Count != job.TotalItemCount)
        {
            throw new InvalidDataException(
                $"Media-validation job '{job.Id}' has an inconsistent asset snapshot.");
        }

        if (expectedAssetIds.Count != normalizedRegistrations.Length ||
            !expectedAssetIds.All(suppliedAssetIds.Contains))
        {
            throw new ArgumentException(
                "The validation batch must contain exactly the media assets snapshotted by the job.",
                nameof(registrations));
        }

        var persistedAssets = new List<StoredMediaAsset>(normalizedRegistrations.Length);
        foreach (var registration in normalizedRegistrations)
        {
            var asset = await ReadMediaAssetByIdAsync(
                    connection,
                    transaction,
                    registration.MediaAssetId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Media asset '{registration.MediaAssetId}' was not found.");
            if (asset.ProfileId != job.ProfileId)
            {
                throw new InvalidOperationException(
                    $"Media asset '{asset.Id}' does not belong to validation job '{job.Id}'.");
            }

            if (asset.State == MediaAssetState.Validated ||
                await MediaValidationResultExistsAsync(
                        connection,
                        transaction,
                        asset.Id,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new MediaValidationConflictException(asset.Id);
            }

            if (registration.Result is not null)
            {
                if (registration.Result.ValidatedAtUtc < job.CreatedAtUtc ||
                    registration.Result.ValidatedAtUtc > completedAtUtc)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(registrations),
                        "A result validation timestamp must fall within its processing job.");
                }

                await InsertMediaValidationResultAsync(
                        connection,
                        transaction,
                        registration.Result,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await SetMediaAssetValidationAsync(
                    connection,
                    transaction,
                    asset.Id,
                    registration.State,
                    registration.FailureMessage,
                    completedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            persistedAssets.Add(asset with
            {
                State = registration.State,
                ValidationFailure = registration.FailureMessage,
                UpdatedAtUtc = completedAtUtc.ToUniversalTime(),
            });
        }

        await SetJobTerminalAsync(
                connection,
                transaction,
                job,
                ProcessingJobState.Completed,
                error: null,
                completedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        var readiness = await DetermineMediaReadinessAsync(
                connection,
                transaction,
                job.ProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        await SetProfileReadinessAsync(
                connection,
                transaction,
                job.ProfileId,
                readiness,
                completedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return persistedAssets;
    }

    public async Task<IReadOnlyList<StoredMediaAsset>> MarkMediaAssetsIntegrityFailedAsync(
        Guid profileId,
        DateTimeOffset expectedUpdatedAtUtc,
        IReadOnlyCollection<Guid> mediaAssetIds,
        DateTimeOffset detectedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(profileId, nameof(profileId));
        ArgumentNullException.ThrowIfNull(mediaAssetIds);
        if (mediaAssetIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one media asset ID is required.",
                nameof(mediaAssetIds));
        }

        var normalizedAssetIds = mediaAssetIds.ToArray();
        if (normalizedAssetIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException(
                "Every media asset requires an ID.",
                nameof(mediaAssetIds));
        }

        if (normalizedAssetIds.Distinct().Count() != normalizedAssetIds.Length)
        {
            throw new ArgumentException(
                "A media asset may appear only once in an integrity-failure batch.",
                nameof(mediaAssetIds));
        }

        if (detectedAtUtc <= expectedUpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(detectedAtUtc),
                "The detection timestamp must be later than the expected profile timestamp.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (await HasActiveJobAsync(connection, transaction, profileId, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new ProfileProcessingActiveException(profileId);
        }

        var assets = new List<StoredMediaAsset>(normalizedAssetIds.Length);
        foreach (var mediaAssetId in normalizedAssetIds)
        {
            var asset = await ReadMediaAssetByIdAsync(
                    connection,
                    transaction,
                    mediaAssetId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Media asset '{mediaAssetId}' was not found.");
            if (asset.ProfileId != profileId)
            {
                throw new InvalidOperationException(
                    $"Media asset '{mediaAssetId}' does not belong to profile '{profileId}'.");
            }

            assets.Add(asset);
        }

        if (!await ProfileHasExpectedTimestampAsync(
                connection,
                transaction,
                profileId,
                expectedUpdatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false))
        {
            if (!await ProfileExistsAsync(connection, transaction, profileId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Profile '{profileId}' was not found.");
            }

            throw new ProfileConcurrencyConflictException(profileId, expectedUpdatedAtUtc);
        }

        foreach (var asset in assets)
        {
            await SetMediaAssetIntegrityFailedAsync(
                    connection,
                    transaction,
                    asset.Id,
                    detectedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var readiness = await DetermineMediaReadinessAsync(
                connection,
                transaction,
                profileId,
                cancellationToken)
            .ConfigureAwait(false);
        await SetProfileReadinessAsync(
                connection,
                transaction,
                profileId,
                readiness,
                detectedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var detectedAt = detectedAtUtc.ToUniversalTime();
        return assets.Select(asset => asset with
        {
            State = MediaAssetState.IntegrityFailed,
            ValidationFailure = MediaIntegrityFailureReason,
            UpdatedAtUtc = detectedAt,
        }).ToArray();
    }

    public async Task<bool> TerminateProcessingJobAsync(
        Guid jobId,
        ProcessingJobState terminalState,
        string? error,
        DateTimeOffset terminatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(jobId, nameof(jobId));
        if (terminalState is not ProcessingJobState.Cancelled and not ProcessingJobState.Failed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminalState),
                terminalState,
                "A job may only be explicitly terminated as cancelled or failed.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var job = await ReadProcessingJobAsync(connection, transaction, jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Processing job '{jobId}' was not found.");
        if (job.State is not ProcessingJobState.Queued and not ProcessingJobState.Running)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        ValidateTimestampNotBefore(terminatedAtUtc, job.UpdatedAtUtc, nameof(terminatedAtUtc));
        var sanitizedError = terminalState == ProcessingJobState.Failed
            ? job.Kind == ProcessingJobKind.MediaValidation
                ? SanitizeValidationFailure(error)
                : SanitizeError(error)
            : null;

        await SetJobTerminalAsync(
                connection,
                transaction,
                job,
                terminalState,
                sanitizedError,
                terminatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await RestoreReadinessAfterTerminatedJobAsync(
                connection,
                transaction,
                job,
                terminatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<StoredMediaAsset>> GetMediaAssetsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(profileId, nameof(profileId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadMediaAssetsAsync(connection, profileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredMediaAsset>> GetMediaAssetsForValidationJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(jobId, nameof(jobId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadMediaAssetsForValidationJobAsync(connection, jobId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredMediaValidationResult?> GetMediaValidationResultAsync(
        Guid mediaAssetId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(mediaAssetId, nameof(mediaAssetId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadMediaValidationResultAsync(
                connection,
                transaction: null,
                mediaAssetId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredMediaValidationResult>> GetMediaValidationResultsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(profileId, nameof(profileId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadMediaValidationResultsAsync(connection, profileId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredProcessingJob>> GetProcessingJobsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(profileId, nameof(profileId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadProcessingJobsAsync(connection, profileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredProcessingJob?> GetProcessingJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(jobId, nameof(jobId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadProcessingJobAsync(connection, transaction: null, jobId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> RecoverInterruptedJobsAsync(
        DateTimeOffset staleBeforeUtc,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (recoveredAtUtc < staleBeforeUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveredAtUtc),
                "The recovery timestamp cannot precede the stale-job threshold.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var activeJobs = await ReadActiveProcessingJobsAsync(
                connection,
                transaction,
                staleBeforeUtc,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var job in activeJobs)
        {
            await SetJobTerminalAsync(
                    connection,
                    transaction,
                    job,
                    ProcessingJobState.Interrupted,
                    "Processing was interrupted before completion.",
                    recoveredAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            await RestoreReadinessAfterTerminatedJobAsync(
                    connection,
                    transaction,
                    job,
                    recoveredAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return activeJobs.Count;
    }

    private static async Task<bool> ProfileExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM profiles WHERE id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ProfileHasExpectedTimestampAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM profiles
            WHERE id = $profileId
              AND updated_utc = $expectedUpdatedUtc;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        command.Parameters.AddWithValue(
            "$expectedUpdatedUtc",
            FormatTimestamp(expectedUpdatedAtUtc));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> HasActiveJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM processing_jobs
            WHERE profile_id = $profileId
              AND state IN ('Queued', 'Running');
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task InsertProcessingJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProcessingJob job,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_jobs (
                id,
                profile_id,
                kind,
                state,
                completed_item_count,
                total_item_count,
                completed_bytes,
                total_bytes,
                workspace_relative_path,
                error,
                created_utc,
                updated_utc)
            VALUES (
                $id,
                $profileId,
                $kind,
                $state,
                $completedItemCount,
                $totalItemCount,
                $completedBytes,
                $totalBytes,
                $workspaceRelativePath,
                $error,
                $createdUtc,
                $updatedUtc);
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        command.Parameters.AddWithValue("$profileId", job.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$kind", job.Kind.ToString());
        command.Parameters.AddWithValue("$state", job.State.ToString());
        command.Parameters.AddWithValue("$completedItemCount", job.CompletedItemCount);
        command.Parameters.AddWithValue("$totalItemCount", job.TotalItemCount);
        command.Parameters.AddWithValue("$completedBytes", job.CompletedBytes);
        command.Parameters.AddWithValue("$totalBytes", job.TotalBytes);
        command.Parameters.AddWithValue("$workspaceRelativePath", job.WorkspaceRelativePath);
        command.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(job.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(job.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredProcessingJob?> ReadProcessingJobAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                profile_id,
                kind,
                state,
                completed_item_count,
                total_item_count,
                completed_bytes,
                total_bytes,
                workspace_relative_path,
                error,
                created_utc,
                updated_utc
            FROM processing_jobs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapProcessingJob(reader)
            : null;
    }

    private static async Task<IReadOnlyList<StoredProcessingJob>> ReadProcessingJobsAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                profile_id,
                kind,
                state,
                completed_item_count,
                total_item_count,
                completed_bytes,
                total_bytes,
                workspace_relative_path,
                error,
                created_utc,
                updated_utc
            FROM processing_jobs
            WHERE profile_id = $profileId
            ORDER BY created_utc, id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        var jobs = new List<StoredProcessingJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(MapProcessingJob(reader));
        }

        return jobs;
    }

    private static async Task<List<StoredProcessingJob>> ReadActiveProcessingJobsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset staleBeforeUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                profile_id,
                kind,
                state,
                completed_item_count,
                total_item_count,
                completed_bytes,
                total_bytes,
                workspace_relative_path,
                error,
                created_utc,
                updated_utc
            FROM processing_jobs
            WHERE state IN ('Queued', 'Running')
              AND updated_utc < $staleBeforeUtc
            ORDER BY created_utc, id;
            """;
        command.Parameters.AddWithValue("$staleBeforeUtc", FormatTimestamp(staleBeforeUtc));
        var jobs = new List<StoredProcessingJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(MapProcessingJob(reader));
        }

        return jobs;
    }

    private static StoredProcessingJob MapProcessingJob(SqliteDataReader reader)
    {
        var kindText = reader.GetString(2);
        if (!Enum.TryParse<ProcessingJobKind>(kindText, ignoreCase: false, out var kind) ||
            !Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Processing job '{reader.GetString(0)}' has unsupported kind '{kindText}'.");
        }

        var stateText = reader.GetString(3);
        if (!Enum.TryParse<ProcessingJobState>(stateText, ignoreCase: false, out var state) ||
            !Enum.IsDefined(state))
        {
            throw new InvalidDataException($"Processing job '{reader.GetString(0)}' has unsupported state '{stateText}'.");
        }

        var completedItemCount = reader.GetInt32(4);
        var totalItemCount = reader.GetInt32(5);
        var completedBytes = reader.GetInt64(6);
        var totalBytes = reader.GetInt64(7);
        try
        {
            ValidateProgress(completedItemCount, totalItemCount, completedBytes, totalBytes);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"Processing job '{reader.GetString(0)}' has invalid stored progress.",
                exception);
        }

        var relativePath = ValidateStoredRelativePath(
            reader.GetString(8),
            "Processing",
            "processing job",
            reader.GetString(0));
        var error = reader.IsDBNull(9) ? null : reader.GetString(9);
        if (!string.Equals(error, SanitizeError(error), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Processing job '{reader.GetString(0)}' has an invalid stored error.");
        }

        return new StoredProcessingJob(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            kind,
            state,
            completedItemCount,
            totalItemCount,
            completedBytes,
            totalBytes,
            relativePath,
            error,
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)));
    }

    private static async Task<bool> HasAnyMediaAssetLinksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM training_videos
            WHERE profile_id = $profileId
              AND media_asset_id IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<bool> HasUnlinkedActiveTrainingVideosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM training_videos
            WHERE profile_id = $profileId
              AND is_archived = 0
              AND media_asset_id IS NULL;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<IReadOnlyList<StoredMediaAsset>> ReadActiveMediaAssetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT
                asset.id,
                asset.profile_id,
                asset.sha256,
                asset.workspace_relative_path,
                asset.byte_length,
                asset.state,
                asset.probe_failure,
                asset.created_utc,
                asset.updated_utc
            FROM training_videos AS video
            INNER JOIN media_assets AS asset ON asset.id = video.media_asset_id
            WHERE video.profile_id = $profileId
              AND video.is_archived = 0
            ORDER BY asset.created_utc, asset.id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        var assets = new List<StoredMediaAsset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assets.Add(MapMediaAsset(reader));
        }

        return assets;
    }

    private static async Task InsertMediaValidationJobAssetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        IEnumerable<Guid> mediaAssetIds,
        CancellationToken cancellationToken)
    {
        foreach (var mediaAssetId in mediaAssetIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO media_validation_job_assets (job_id, media_asset_id)
                VALUES ($jobId, $mediaAssetId);
                """;
            command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            command.Parameters.AddWithValue("$mediaAssetId", mediaAssetId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<Guid>> ReadMediaValidationJobAssetIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT media_asset_id
            FROM media_validation_job_assets
            WHERE job_id = $jobId
            ORDER BY media_asset_id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        var assetIds = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assetIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return assetIds;
    }

    private static async Task<IReadOnlyList<StoredMediaAsset>> ReadMediaAssetsForValidationJobAsync(
        SqliteConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                asset.id,
                asset.profile_id,
                asset.sha256,
                asset.workspace_relative_path,
                asset.byte_length,
                asset.state,
                asset.probe_failure,
                asset.created_utc,
                asset.updated_utc
            FROM media_validation_job_assets AS item
            INNER JOIN media_assets AS asset ON asset.id = item.media_asset_id
            WHERE item.job_id = $jobId
            ORDER BY asset.created_utc, asset.id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        var assets = new List<StoredMediaAsset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assets.Add(MapMediaAsset(reader));
        }

        return assets;
    }

    private static async Task<StoredMediaAsset?> ReadMediaAssetByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, profile_id, sha256, workspace_relative_path, byte_length, state,
                   probe_failure, created_utc, updated_utc
            FROM media_assets
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", mediaAssetId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapMediaAsset(reader)
            : null;
    }

    private static async Task<bool> MediaValidationResultExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM media_validation_results
            WHERE media_asset_id = $mediaAssetId;
            """;
        command.Parameters.AddWithValue("$mediaAssetId", mediaAssetId.ToString("D"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task SetMediaAssetValidationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mediaAssetId,
        MediaAssetState state,
        string? failure,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE media_assets
            SET state = $state,
                probe_failure = $probeFailure,
                updated_utc = $updatedUtc
            WHERE id = $id
              AND state IN ('AwaitingProbe', 'ValidationFailed');
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$probeFailure", (object?)failure ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", mediaAssetId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new MediaValidationConflictException(mediaAssetId);
        }
    }

    private static async Task SetMediaAssetIntegrityFailedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mediaAssetId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var failureReason = SanitizeValidationFailure(MediaIntegrityFailureReason)
            ?? throw new InvalidOperationException("The media-integrity failure reason is invalid.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE media_assets
            SET state = $state,
                probe_failure = $probeFailure,
                updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", MediaAssetState.IntegrityFailed.ToString());
        command.Parameters.AddWithValue("$probeFailure", failureReason);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", mediaAssetId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Media asset '{mediaAssetId}' was not found.");
        }
    }

    private static async Task InsertMediaValidationResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMediaValidationResult result,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO media_validation_results (
                media_asset_id,
                container_format,
                container_major_brand,
                video_stream_index,
                video_codec,
                width,
                height,
                audio_stream_index,
                audio_codec,
                audio_sample_rate_hz,
                audio_channel_count,
                duration_microseconds,
                frame_rate_numerator,
                frame_rate_denominator,
                ffprobe_version,
                ffprobe_compiler_identifier,
                ffprobe_configuration,
                ffprobe_configuration_sha256,
                ffprobe_executable_sha256,
                ffmpeg_version,
                ffmpeg_compiler_identifier,
                ffmpeg_configuration,
                ffmpeg_configuration_sha256,
                ffmpeg_executable_sha256,
                validation_contract_sha256,
                decode_completed,
                decoded_duration_microseconds,
                validated_utc)
            VALUES (
                $mediaAssetId,
                $containerFormat,
                $containerMajorBrand,
                $videoStreamIndex,
                $videoCodec,
                $width,
                $height,
                $audioStreamIndex,
                $audioCodec,
                $audioSampleRateHz,
                $audioChannelCount,
                $durationMicroseconds,
                $frameRateNumerator,
                $frameRateDenominator,
                $ffprobeVersion,
                $ffprobeCompilerIdentifier,
                $ffprobeConfiguration,
                $ffprobeConfigurationSha256,
                $ffprobeExecutableSha256,
                $ffmpegVersion,
                $ffmpegCompilerIdentifier,
                $ffmpegConfiguration,
                $ffmpegConfigurationSha256,
                $ffmpegExecutableSha256,
                $validationContractSha256,
                $decodeCompleted,
                $decodedDurationMicroseconds,
                $validatedUtc);
            """;
        command.Parameters.AddWithValue("$mediaAssetId", result.MediaAssetId.ToString("D"));
        command.Parameters.AddWithValue("$containerFormat", result.ContainerFormat);
        command.Parameters.AddWithValue(
            "$containerMajorBrand",
            (object?)result.ContainerMajorBrand ?? DBNull.Value);
        command.Parameters.AddWithValue("$videoStreamIndex", result.VideoStreamIndex);
        command.Parameters.AddWithValue("$videoCodec", result.VideoCodec);
        command.Parameters.AddWithValue("$width", result.Width);
        command.Parameters.AddWithValue("$height", result.Height);
        command.Parameters.AddWithValue("$audioStreamIndex", result.AudioStreamIndex);
        command.Parameters.AddWithValue("$audioCodec", result.AudioCodec);
        command.Parameters.AddWithValue("$audioSampleRateHz", result.AudioSampleRateHz);
        command.Parameters.AddWithValue("$audioChannelCount", result.AudioChannelCount);
        command.Parameters.AddWithValue("$durationMicroseconds", result.DurationMicroseconds);
        command.Parameters.AddWithValue("$frameRateNumerator", result.FrameRateNumerator);
        command.Parameters.AddWithValue("$frameRateDenominator", result.FrameRateDenominator);
        command.Parameters.AddWithValue("$ffprobeVersion", result.FfprobeVersion);
        command.Parameters.AddWithValue("$ffprobeCompilerIdentifier", result.FfprobeCompilerIdentifier);
        command.Parameters.AddWithValue("$ffprobeConfiguration", result.FfprobeConfiguration);
        command.Parameters.AddWithValue("$ffprobeConfigurationSha256", result.FfprobeConfigurationSha256);
        command.Parameters.AddWithValue("$ffprobeExecutableSha256", result.FfprobeExecutableSha256);
        command.Parameters.AddWithValue("$ffmpegVersion", result.FfmpegVersion);
        command.Parameters.AddWithValue("$ffmpegCompilerIdentifier", result.FfmpegCompilerIdentifier);
        command.Parameters.AddWithValue("$ffmpegConfiguration", result.FfmpegConfiguration);
        command.Parameters.AddWithValue("$ffmpegConfigurationSha256", result.FfmpegConfigurationSha256);
        command.Parameters.AddWithValue("$ffmpegExecutableSha256", result.FfmpegExecutableSha256);
        command.Parameters.AddWithValue("$validationContractSha256", result.ValidationContractSha256);
        command.Parameters.AddWithValue("$decodeCompleted", result.DecodeCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$decodedDurationMicroseconds", result.DecodedDurationMicroseconds);
        command.Parameters.AddWithValue("$validatedUtc", FormatTimestamp(result.ValidatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredMediaValidationResult?> ReadMediaValidationResultAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                media_asset_id,
                container_format,
                container_major_brand,
                video_stream_index,
                video_codec,
                width,
                height,
                audio_stream_index,
                audio_codec,
                audio_sample_rate_hz,
                audio_channel_count,
                duration_microseconds,
                frame_rate_numerator,
                frame_rate_denominator,
                ffprobe_version,
                ffprobe_compiler_identifier,
                ffprobe_configuration,
                ffprobe_configuration_sha256,
                ffprobe_executable_sha256,
                ffmpeg_version,
                ffmpeg_compiler_identifier,
                ffmpeg_configuration,
                ffmpeg_configuration_sha256,
                ffmpeg_executable_sha256,
                validation_contract_sha256,
                decode_completed,
                decoded_duration_microseconds,
                validated_utc
            FROM media_validation_results
            WHERE media_asset_id = $mediaAssetId;
            """;
        command.Parameters.AddWithValue("$mediaAssetId", mediaAssetId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapMediaValidationResult(reader)
            : null;
    }

    private static async Task<IReadOnlyList<StoredMediaValidationResult>> ReadMediaValidationResultsAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                result.media_asset_id,
                result.container_format,
                result.container_major_brand,
                result.video_stream_index,
                result.video_codec,
                result.width,
                result.height,
                result.audio_stream_index,
                result.audio_codec,
                result.audio_sample_rate_hz,
                result.audio_channel_count,
                result.duration_microseconds,
                result.frame_rate_numerator,
                result.frame_rate_denominator,
                result.ffprobe_version,
                result.ffprobe_compiler_identifier,
                result.ffprobe_configuration,
                result.ffprobe_configuration_sha256,
                result.ffprobe_executable_sha256,
                result.ffmpeg_version,
                result.ffmpeg_compiler_identifier,
                result.ffmpeg_configuration,
                result.ffmpeg_configuration_sha256,
                result.ffmpeg_executable_sha256,
                result.validation_contract_sha256,
                result.decode_completed,
                result.decoded_duration_microseconds,
                result.validated_utc
            FROM media_validation_results AS result
            INNER JOIN media_assets AS asset ON asset.id = result.media_asset_id
            WHERE asset.profile_id = $profileId
            ORDER BY result.validated_utc, result.media_asset_id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        var results = new List<StoredMediaValidationResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapMediaValidationResult(reader));
        }

        return results;
    }

    private static StoredMediaValidationResult MapMediaValidationResult(SqliteDataReader reader)
    {
        var result = new StoredMediaValidationResult(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23),
            reader.GetString(24),
            reader.GetInt32(25) == 1,
            reader.GetInt64(26),
            ParseTimestamp(reader.GetString(27)));
        try
        {
            ValidateMediaValidationResult(result);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Media asset '{result.MediaAssetId}' has an invalid stored validation result.",
                exception);
        }

        return result;
    }

    private static async Task<StoredMediaAsset?> ReadMediaAssetByHashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        string sha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, profile_id, sha256, workspace_relative_path, byte_length, state,
                   probe_failure, created_utc, updated_utc
            FROM media_assets
            WHERE profile_id = $profileId
              AND sha256 = $sha256;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        command.Parameters.AddWithValue("$sha256", sha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapMediaAsset(reader)
            : null;
    }

    private static async Task<StoredMediaAsset?> ReadMediaAssetByPathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        string workspaceRelativePath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, profile_id, sha256, workspace_relative_path, byte_length, state,
                   probe_failure, created_utc, updated_utc
            FROM media_assets
            WHERE profile_id = $profileId
              AND workspace_relative_path = $workspaceRelativePath COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        command.Parameters.AddWithValue("$workspaceRelativePath", workspaceRelativePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapMediaAsset(reader)
            : null;
    }

    private static async Task<IReadOnlyList<StoredMediaAsset>> ReadMediaAssetsAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, profile_id, sha256, workspace_relative_path, byte_length, state,
                   probe_failure, created_utc, updated_utc
            FROM media_assets
            WHERE profile_id = $profileId
            ORDER BY created_utc, id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        var assets = new List<StoredMediaAsset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assets.Add(MapMediaAsset(reader));
        }

        return assets;
    }

    private static StoredMediaAsset MapMediaAsset(SqliteDataReader reader)
    {
        var hash = reader.GetString(2);
        if (!IsLowercaseSha256(hash))
        {
            throw new InvalidDataException($"Media asset '{reader.GetString(0)}' has an invalid SHA-256 hash.");
        }

        var byteLength = reader.GetInt64(4);
        if (byteLength < 0)
        {
            throw new InvalidDataException($"Media asset '{reader.GetString(0)}' has an invalid byte length.");
        }

        var stateText = reader.GetString(5);
        if (!Enum.TryParse<MediaAssetState>(stateText, ignoreCase: false, out var state) ||
            !Enum.IsDefined(state))
        {
            throw new InvalidDataException($"Media asset '{reader.GetString(0)}' has unsupported state '{stateText}'.");
        }

        var probeFailure = reader.IsDBNull(6) ? null : reader.GetString(6);
        if (!string.Equals(probeFailure, SanitizeValidationFailure(probeFailure), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Media asset '{reader.GetString(0)}' has an invalid stored probe failure.");
        }

        if ((state is MediaAssetState.ValidationFailed or MediaAssetState.IntegrityFailed) !=
            (probeFailure is not null))
        {
            throw new InvalidDataException(
                $"Media asset '{reader.GetString(0)}' has inconsistent probe status and failure detail.");
        }

        return new StoredMediaAsset(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            hash,
            ValidateStoredRelativePath(
                reader.GetString(3),
                "Media",
                "media asset",
                reader.GetString(0)),
            byteLength,
            state,
            probeFailure,
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)));
    }

    private static async Task InsertMediaAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMediaAsset asset,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO media_assets (
                id, profile_id, sha256, workspace_relative_path, byte_length, state, probe_failure,
                created_utc, updated_utc)
            VALUES (
                $id, $profileId, $sha256, $workspaceRelativePath, $byteLength, $state, $probeFailure,
                $createdUtc, $updatedUtc);
            """;
        command.Parameters.AddWithValue("$id", asset.Id.ToString("D"));
        command.Parameters.AddWithValue("$profileId", asset.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$sha256", asset.Sha256);
        command.Parameters.AddWithValue("$workspaceRelativePath", asset.WorkspaceRelativePath);
        command.Parameters.AddWithValue("$byteLength", asset.ByteLength);
        command.Parameters.AddWithValue("$state", asset.State.ToString());
        command.Parameters.AddWithValue("$probeFailure", (object?)asset.ValidationFailure ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(asset.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(asset.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<Guid, TrainingCondition>> ReadTrainingVideoConditionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        IEnumerable<Guid> trainingVideoIds,
        CancellationToken cancellationToken)
    {
        var conditions = new Dictionary<Guid, TrainingCondition>();
        foreach (var trainingVideoId in trainingVideoIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT training_condition
                FROM training_videos
                WHERE id = $id
                  AND profile_id = $profileId
                  AND is_archived = 0
                  AND media_asset_id IS NULL;
                """;
            command.Parameters.AddWithValue("$id", trainingVideoId.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            var conditionText = (string?)await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (conditionText is null)
            {
                throw new KeyNotFoundException(
                    $"Training video '{trainingVideoId}' is not an active, unlinked item in profile '{profileId}'.");
            }

            if (!Enum.TryParse<TrainingCondition>(conditionText, ignoreCase: false, out var condition) ||
                !Enum.IsDefined(condition))
            {
                throw new InvalidDataException(
                    $"Training video '{trainingVideoId}' has unsupported condition '{conditionText}'.");
            }

            conditions.Add(trainingVideoId, condition);
        }

        return conditions;
    }

    private static async Task<IReadOnlyList<TrainingCondition>> ReadLinkedConditionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT training_condition
            FROM training_videos
            WHERE media_asset_id = $mediaAssetId;
            """;
        command.Parameters.AddWithValue("$mediaAssetId", mediaAssetId.ToString("D"));
        var conditions = new List<TrainingCondition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = reader.GetString(0);
            if (!Enum.TryParse<TrainingCondition>(text, ignoreCase: false, out var condition) ||
                !Enum.IsDefined(condition))
            {
                throw new InvalidDataException(
                    $"Media asset '{mediaAssetId}' is linked to unsupported condition '{text}'.");
            }

            conditions.Add(condition);
        }

        return conditions;
    }

    private static async Task LinkTrainingVideoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid trainingVideoId,
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE training_videos
            SET media_asset_id = $mediaAssetId
            WHERE id = $trainingVideoId
              AND is_archived = 0
              AND media_asset_id IS NULL;
            """;
        command.Parameters.AddWithValue("$mediaAssetId", mediaAssetId.ToString("D"));
        command.Parameters.AddWithValue("$trainingVideoId", trainingVideoId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Training video '{trainingVideoId}' could not be linked.");
        }
    }

    private static async Task SetJobTerminalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProcessingJob job,
        ProcessingJobState terminalState,
        string? error,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var completedItems = terminalState == ProcessingJobState.Completed
            ? job.TotalItemCount
            : job.CompletedItemCount;
        var completedBytes = terminalState == ProcessingJobState.Completed
            ? job.TotalBytes
            : job.CompletedBytes;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processing_jobs
            SET state = $state,
                completed_item_count = $completedItemCount,
                completed_bytes = $completedBytes,
                error = $error,
                updated_utc = $updatedUtc
            WHERE id = $id
              AND state IN ('Queued', 'Running');
            """;
        command.Parameters.AddWithValue("$state", terminalState.ToString());
        command.Parameters.AddWithValue("$completedItemCount", completedItems);
        command.Parameters.AddWithValue("$completedBytes", completedBytes);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Processing job '{job.Id}' is no longer active.");
        }
    }

    private static async Task SetProfileReadinessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        ProfileReadiness readiness,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE profiles
            SET readiness = $readiness,
                updated_utc = $updatedUtc
            WHERE id = $profileId;
            """;
        command.Parameters.AddWithValue("$readiness", readiness.ToString());
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Profile '{profileId}' was not found.");
        }
    }

    private static async Task RestoreReadinessAfterTerminatedJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProcessingJob job,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var readiness = job.Kind == ProcessingJobKind.MediaValidation
            ? await DetermineMediaReadinessAsync(
                    connection,
                    transaction,
                    job.ProfileId,
                    cancellationToken)
                .ConfigureAwait(false)
            : ProfileReadiness.Draft;
        await SetProfileReadinessAsync(
                connection,
                transaction,
                job.ProfileId,
                readiness,
                timestamp,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ProfileReadiness> DetermineMediaReadinessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE WHEN video.media_asset_id IS NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN asset.state = 'AwaitingProbe' THEN 1 ELSE 0 END),
                SUM(CASE WHEN asset.state = 'ValidationFailed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN asset.state = 'Validated' THEN 1 ELSE 0 END),
                SUM(CASE WHEN asset.state = 'IntegrityFailed' THEN 1 ELSE 0 END)
            FROM training_videos AS video
            LEFT JOIN media_assets AS asset ON asset.id = video.media_asset_id
            WHERE video.profile_id = $profileId
              AND video.is_archived = 0;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException($"Unable to determine media readiness for profile '{profileId}'.");
        }

        var activeCount = reader.GetInt64(0);
        var unlinkedCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        var awaitingCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
        var failedCount = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
        var validatedCount = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
        var integrityFailedCount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
        if (integrityFailedCount != 0)
        {
            return ProfileReadiness.MediaIntegrityFailed;
        }

        if (activeCount == 0 || unlinkedCount != 0)
        {
            return ProfileReadiness.Draft;
        }

        if (awaitingCount != 0)
        {
            return ProfileReadiness.MediaIngestedAwaitingProbe;
        }

        if (failedCount != 0)
        {
            return ProfileReadiness.MediaValidationFailed;
        }

        if (validatedCount == activeCount)
        {
            return ProfileReadiness.MediaValidated;
        }

        throw new InvalidDataException($"Profile '{profileId}' has inconsistent media validation states.");
    }

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "PRAGMA secure_delete = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task ApplyMigrationStepAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string migrationSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = migrationSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO profiles (
                id,
                display_name,
                workspace_root,
                download_staging_root,
                readiness,
                created_utc,
                updated_utc)
            VALUES (
                $id,
                $displayName,
                $workspaceRoot,
                $downloadStagingRoot,
                $readiness,
                $createdUtc,
                $updatedUtc);
            """;
        AddProfileParameters(command, profile, includeCreatedTimestamp: true);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateProfileRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE profiles
            SET display_name = $displayName,
                readiness = $readiness,
                updated_utc = $updatedUtc
            WHERE id = $id
              AND updated_utc = $expectedUpdatedUtc;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.AddWithValue("$readiness", profile.Readiness);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expectedUpdatedUtc", FormatTimestamp(expectedUpdatedAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddProfileParameters(
        SqliteCommand command,
        StoredProfile profile,
        bool includeCreatedTimestamp)
    {
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.AddWithValue("$workspaceRoot", profile.WorkspaceRoot);
        command.Parameters.AddWithValue(
            "$downloadStagingRoot",
            (object?)profile.DownloadStagingRoot ?? DBNull.Value);
        command.Parameters.AddWithValue("$readiness", profile.Readiness);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(profile.UpdatedAtUtc));

        if (includeCreatedTimestamp)
        {
            command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(profile.CreatedAtUtc));
        }
    }

    private static async Task DeleteTrainingVideosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM training_videos WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureIdentityAndLocationsAllowedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, display_name, workspace_root, download_staging_root FROM profiles;";

        var targetFound = false;
        string? storedWorkspaceRoot = null;
        string? storedDownloadStagingRoot = null;
        var nameConflict = false;
        var workspaceConflict = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existingId = Guid.Parse(reader.GetString(0));
            if (isUpdate && existingId == profile.Id)
            {
                targetFound = true;
                storedWorkspaceRoot = reader.GetString(2);
                storedDownloadStagingRoot = reader.IsDBNull(3) ? null : reader.GetString(3);
                continue;
            }

            nameConflict |= string.Equals(
                reader.GetString(1),
                profile.DisplayName,
                StringComparison.OrdinalIgnoreCase);
            workspaceConflict |= string.Equals(
                reader.GetString(2),
                profile.WorkspaceRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        if (isUpdate)
        {
            if (!targetFound)
            {
                throw new KeyNotFoundException($"Profile '{profile.Id}' was not found.");
            }

            if (!string.Equals(
                    storedWorkspaceRoot,
                    profile.WorkspaceRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    storedDownloadStagingRoot,
                    profile.DownloadStagingRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A saved profile's workspace and download locations cannot be changed.");
            }
        }

        if (nameConflict)
        {
            throw new ProfileNameConflictException(
                profile.DisplayName,
                new InvalidOperationException("The display-name conflict was detected before writing."));
        }

        if (workspaceConflict)
        {
            throw new ProfileWorkspaceConflictException(
                profile.WorkspaceRoot,
                new InvalidOperationException("The workspace conflict was detected before writing."));
        }
    }

    private static async Task InsertTrainingVideosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        CancellationToken cancellationToken)
    {
        await ValidateMediaAssetLinksAsync(
                connection,
                transaction,
                profile,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var video in profile.TrainingVideos)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO training_videos (
                    id,
                    profile_id,
                    file_path,
                    recording_date_label,
                    training_condition,
                    is_archived,
                    sort_order,
                    media_asset_id)
                VALUES (
                    $id,
                    $profileId,
                    $filePath,
                    $recordingDateLabel,
                    $trainingCondition,
                    $isArchived,
                    $sortOrder,
                    $mediaAssetId);
                """;
            command.Parameters.AddWithValue("$id", video.Id.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$filePath", video.FilePath);
            command.Parameters.AddWithValue("$recordingDateLabel", video.RecordingDateLabel);
            command.Parameters.AddWithValue("$trainingCondition", video.Condition.ToString());
            command.Parameters.AddWithValue("$isArchived", video.IsArchived ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", video.SortOrder);
            command.Parameters.AddWithValue(
                "$mediaAssetId",
                video.MediaAssetId is Guid mediaAssetId
                    ? mediaAssetId.ToString("D")
                    : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ValidateMediaAssetLinksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        CancellationToken cancellationToken)
    {
        foreach (var linkedGroup in profile.TrainingVideos
                     .Where(video => video.MediaAssetId.HasValue)
                     .GroupBy(video => video.MediaAssetId!.Value))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT profile_id, sha256 FROM media_assets WHERE id = $id;";
            command.Parameters.AddWithValue("$id", linkedGroup.Key.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                Guid.Parse(reader.GetString(0)) != profile.Id)
            {
                throw new InvalidOperationException(
                    $"Media asset '{linkedGroup.Key}' does not belong to profile '{profile.Id}'.");
            }

            var sha256 = reader.GetString(1);
            var conditions = linkedGroup.Select(video => video.Condition).Distinct().ToArray();
            if (conditions.Length > 1)
            {
                throw new MediaAssetConditionConflictException(
                    sha256,
                    conditions[0],
                    conditions[1]);
            }
        }
    }

    private static async Task<StoredProfile?> ReadProfileAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                display_name,
                workspace_root,
                download_staging_root,
                readiness,
                created_utc,
                updated_utc
            FROM profiles
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapProfile(reader)
            : null;
    }

    private static async Task<List<StoredProfile>> ReadProfilesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                display_name,
                workspace_root,
                download_staging_root,
                readiness,
                created_utc,
                updated_utc
            FROM profiles
            ORDER BY display_name COLLATE NOCASE, id;
            """;

        var profiles = new List<StoredProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(MapProfile(reader));
        }

        return profiles;
    }

    private static StoredProfile MapProfile(SqliteDataReader reader)
    {
        return new StoredProfile(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            []);
    }

    private static async Task<IReadOnlyList<StoredTrainingVideo>> ReadTrainingVideosAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                file_path,
                recording_date_label,
                training_condition,
                is_archived,
                sort_order,
                media_asset_id
            FROM training_videos
            WHERE profile_id = $profileId
            ORDER BY sort_order, id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));

        var videos = new List<StoredTrainingVideo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var conditionText = reader.GetString(3);
            if (!Enum.TryParse<TrainingCondition>(conditionText, ignoreCase: false, out var condition) ||
                !Enum.IsDefined(condition))
            {
                throw new InvalidDataException(
                    $"Training video '{reader.GetString(0)}' has unsupported condition '{conditionText}'.");
            }

            var storedPath = reader.GetString(1);
            string fullPath;
            try
            {
                if (string.IsNullOrWhiteSpace(storedPath) || !Path.IsPathFullyQualified(storedPath))
                {
                    throw new ArgumentException("The path is not absolute.", nameof(storedPath));
                }

                fullPath = Path.GetFullPath(storedPath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException(
                    $"Training video '{reader.GetString(0)}' has an invalid stored path.",
                    exception);
            }

            videos.Add(new StoredTrainingVideo(
                Guid.Parse(reader.GetString(0)),
                fullPath,
                reader.GetString(2),
                condition,
                reader.GetInt64(4) != 0,
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))));
        }

        return videos;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseTimestamp(string timestamp)
    {
        return DateTimeOffset.ParseExact(
            timestamp,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private static bool IsDisplayNameConflict(SqliteException exception)
    {
        const int constraintError = 19;
        const int uniqueConstraint = 2067;

        return exception.SqliteErrorCode == constraintError &&
               exception.SqliteExtendedErrorCode == uniqueConstraint &&
               exception.Message.Contains("profiles.display_name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkspaceRootConflict(SqliteException exception)
    {
        const int constraintError = 19;
        const int uniqueConstraint = 2067;

        return exception.SqliteErrorCode == constraintError &&
               exception.SqliteExtendedErrorCode == uniqueConstraint &&
               exception.Message.Contains("profiles.workspace_root", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty ID is required.", parameterName);
        }
    }

    private static void ValidateRegistration(MediaAssetRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateRequiredId(registration.TrainingVideoId, nameof(registration.TrainingVideoId));
        ValidateRequiredId(registration.MediaAssetId, nameof(registration.MediaAssetId));
        if (!IsLowercaseSha256(registration.Sha256))
        {
            throw new ArgumentException(
                "A SHA-256 value must contain exactly 64 lowercase hexadecimal characters.",
                nameof(registration));
        }

        _ = NormalizeBoundedWorkspaceRelativePath(
            registration.WorkspaceRelativePath,
            "Media",
            nameof(registration));
        if (registration.ByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registration),
                registration.ByteLength,
                "A media asset must contain at least one byte.");
        }
    }

    private static void ValidateMediaValidationRegistration(MediaValidationRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateRequiredId(registration.MediaAssetId, nameof(registration.MediaAssetId));
        if (registration.State == MediaAssetState.Validated)
        {
            if (registration.Result is null || registration.Result.MediaAssetId != registration.MediaAssetId)
            {
                throw new ArgumentException(
                    "A successful validation requires a matching normalized result.",
                    nameof(registration));
            }

            if (registration.FailureMessage is not null)
            {
                throw new ArgumentException(
                    "A successful validation cannot include a failure message.",
                    nameof(registration));
            }

            ValidateMediaValidationResult(registration.Result);
            return;
        }

        if (registration.State == MediaAssetState.ValidationFailed)
        {
            if (registration.Result is not null || registration.FailureMessage is null)
            {
                throw new ArgumentException(
                    "A failed validation requires a sanitized failure message and no result.",
                    nameof(registration));
            }

            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(registration),
            registration.State,
            "A validation result must be either validated or failed.");
    }

    private static void ValidateMediaValidationResult(StoredMediaValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateRequiredId(result.MediaAssetId, nameof(result.MediaAssetId));
        ValidateProbeText(result.ContainerFormat, nameof(result.ContainerFormat));
        if (result.ContainerMajorBrand is not null)
        {
            ValidateProbeText(result.ContainerMajorBrand, nameof(result.ContainerMajorBrand));
        }

        ValidateProbeText(result.VideoCodec, nameof(result.VideoCodec));
        ValidateProbeText(result.AudioCodec, nameof(result.AudioCodec));
        ValidateProbeText(result.FfprobeVersion, nameof(result.FfprobeVersion));
        ValidateProbeText(result.FfprobeCompilerIdentifier, nameof(result.FfprobeCompilerIdentifier));
        ValidateProbeText(result.FfprobeConfiguration, nameof(result.FfprobeConfiguration));
        ValidateProbeText(result.FfmpegVersion, nameof(result.FfmpegVersion));
        ValidateProbeText(result.FfmpegCompilerIdentifier, nameof(result.FfmpegCompilerIdentifier));
        ValidateProbeText(result.FfmpegConfiguration, nameof(result.FfmpegConfiguration));

        if (result.VideoStreamIndex < 0 || result.AudioStreamIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(result), "Stream indices cannot be negative.");
        }

        if (result.Width <= 0 || result.Height <= 0 ||
            result.AudioSampleRateHz <= 0 || result.AudioChannelCount <= 0 ||
            result.DurationMicroseconds <= 0 ||
            result.FrameRateNumerator <= 0 || result.FrameRateDenominator <= 0 ||
            !result.DecodeCompleted || result.DecodedDurationMicroseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(result),
                "Validated media dimensions, timing, frame rate, audio, and decode values must be positive.");
        }

        if (!IsLowercaseSha256(result.FfprobeConfigurationSha256) ||
            !IsLowercaseSha256(result.FfprobeExecutableSha256) ||
            !IsLowercaseSha256(result.FfmpegConfigurationSha256) ||
            !IsLowercaseSha256(result.FfmpegExecutableSha256) ||
            !IsLowercaseSha256(result.ValidationContractSha256))
        {
            throw new ArgumentException(
                "Validation provenance hashes must be lowercase SHA-256 values.",
                nameof(result));
        }

        if (result.ValidatedAtUtc == default)
        {
            throw new ArgumentException("A validation timestamp is required.", nameof(result));
        }
    }

    private static void ValidateProbeText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaximumProbeTextLength || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Validation provenance text is too long or contains control characters.",
                parameterName);
        }
    }

    private static void ValidateProgress(
        int completedItemCount,
        int totalItemCount,
        long completedBytes,
        long totalBytes)
    {
        if (totalItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalItemCount),
                "The total item count cannot be negative.");
        }

        if (completedItemCount < 0 || completedItemCount > totalItemCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedItemCount),
                "The completed item count must be between zero and the total item count.");
        }

        if (totalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalBytes),
                "The total byte count cannot be negative.");
        }

        if (completedBytes < 0 || completedBytes > totalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedBytes),
                "The completed byte count must be between zero and the total byte count.");
        }
    }

    private static void ValidateTimestampNotBefore(
        DateTimeOffset timestamp,
        DateTimeOffset minimum,
        string parameterName)
    {
        if (timestamp < minimum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The timestamp cannot precede the job's latest persisted update.");
        }
    }

    private static bool IsLowercaseSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeWorkspaceRelativePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.Length > 1_024 || path.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The workspace-relative path is too long or contains control characters.",
                parameterName);
        }

        var slashPath = path.Replace('\\', '/');
        if (slashPath.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be relative to the profile workspace.", parameterName);
        }

        var segments = slashPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var invalidFileNameCharacters = Path.GetInvalidFileNameChars();
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment is "." or ".." ||
                string.IsNullOrWhiteSpace(segment) ||
                !string.Equals(segment, segment.Trim(), StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.IndexOfAny(invalidFileNameCharacters) >= 0))
        {
            throw new ArgumentException(
                "The workspace-relative path contains an invalid or traversing segment.",
                parameterName);
        }

        return string.Join('/', segments);
    }

    private static string NormalizeBoundedWorkspaceRelativePath(
        string path,
        string requiredTopLevelDirectory,
        string parameterName)
    {
        var normalized = NormalizeWorkspaceRelativePath(path, parameterName);
        var separatorIndex = normalized.IndexOf('/');
        if (separatorIndex <= 0 ||
            !string.Equals(
                normalized[..separatorIndex],
                requiredTopLevelDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The path must be beneath the top-level {requiredTopLevelDirectory} directory.",
                parameterName);
        }

        return requiredTopLevelDirectory + normalized[separatorIndex..];
    }

    private static string ValidateStoredRelativePath(
        string path,
        string requiredTopLevelDirectory,
        string recordKind,
        string recordId)
    {
        try
        {
            var normalized = NormalizeBoundedWorkspaceRelativePath(
                path,
                requiredTopLevelDirectory,
                nameof(path));
            if (!string.Equals(path, normalized, StringComparison.Ordinal))
            {
                throw new ArgumentException("The path is not normalized.", nameof(path));
            }

            return normalized;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Stored {recordKind} '{recordId}' has an invalid workspace-relative path.",
                exception);
        }
    }

    private static string? SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var sanitized = new string(error
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Trim();
        if (sanitized.Length > MaximumStoredErrorLength)
        {
            sanitized = sanitized[..MaximumStoredErrorLength];
        }

        return sanitized.Length == 0 ? null : sanitized;
    }

    private static string? SanitizeValidationFailure(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return null;
        }

        var trimmed = failure.Trim();
        var resemblesRawOutput = trimmed.Length > 512 ||
            trimmed.Any(char.IsControl) ||
            trimmed.IndexOfAny(['{', '}', '[', ']', '\\']) >= 0 ||
            trimmed.Contains("/", StringComparison.Ordinal) ||
            trimmed.Contains(":\\", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("file:", StringComparison.OrdinalIgnoreCase);
        if (resemblesRawOutput)
        {
            return "Media validation failed; detailed tool output was not retained.";
        }

        var sanitized = new string(trimmed
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        if (sanitized.Length > MaximumStoredErrorLength)
        {
            sanitized = sanitized[..MaximumStoredErrorLength];
        }

        return sanitized.Length == 0 ? null : sanitized;
    }

    private static void ValidateProfile(StoredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Id == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(profile));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.WorkspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Readiness);
        ArgumentNullException.ThrowIfNull(profile.TrainingVideos);

        var videoIds = new HashSet<Guid>();
        foreach (var video in profile.TrainingVideos)
        {
            if (video.Id == Guid.Empty)
            {
                throw new ArgumentException("Every training video requires an ID.", nameof(profile));
            }

            if (!videoIds.Add(video.Id))
            {
                throw new ArgumentException(
                    $"Training video ID '{video.Id}' appears more than once.",
                    nameof(profile));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(video.FilePath);
            ArgumentNullException.ThrowIfNull(video.RecordingDateLabel);

            if (!Enum.IsDefined(video.Condition))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    video.Condition,
                    "The training condition is unsupported.");
            }

            if (video.SortOrder < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    video.SortOrder,
                    "Training video order cannot be negative.");
            }

            if (video.MediaAssetId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A linked media asset ID cannot be empty.",
                    nameof(profile));
            }
        }
    }
}
