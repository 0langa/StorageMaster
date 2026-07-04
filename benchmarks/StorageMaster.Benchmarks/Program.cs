using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
public sealed class DuplicateHashBenchmarks
{
    private readonly FileContentHasher _hasher = new();
    private string _tempDir = string.Empty;
    private string _binaryPath = string.Empty;
    private string _textPath = string.Empty;
    private FileEntry _textEntry = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sm_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _binaryPath = Path.Combine(_tempDir, "payload.bin");
        _textPath = Path.Combine(_tempDir, "payload.txt");

        var bytes = new byte[8 * 1024 * 1024];
        new Random(1234).NextBytes(bytes);
        File.WriteAllBytes(_binaryPath, bytes);
        File.WriteAllText(_textPath, string.Join(Environment.NewLine, Enumerable.Range(0, 100_000).Select(i => $"line {i}   ")));

        _textEntry = new FileEntry
        {
            Id = 1,
            SessionId = 1,
            FullPath = _textPath,
            FileName = Path.GetFileName(_textPath),
            Extension = ".txt",
            SizeBytes = new FileInfo(_textPath).Length,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = File.GetLastWriteTimeUtc(_textPath),
            AccessedUtc = DateTime.UtcNow,
            Attributes = FileAttributes.Normal,
            Category = FileTypeCategory.Document,
            IsReparsePoint = false,
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Benchmark]
    public Task<string> Sha256_8MiB() => _hasher.ComputeSha256Async(_binaryPath);

    [Benchmark]
    public Task<string> PartialHash_8MiB() => _hasher.ComputePartialHashAsync(_binaryPath);

    [Benchmark]
    public Task<DuplicateSignature> NormalizedText_100kLines()
    {
        var strategy = new NormalizedTextStrategy();
        return strategy.ComputeSignatureAsync(new DuplicateCandidate(_textEntry));
    }
}

[MemoryDiagnoser]
public sealed class DuplicateStorageBenchmarks
{
    private string _dbPath = string.Empty;
    private StorageDbContext _db = null!;
    private ScanRepository _scanRepository = null!;
    private DuplicateRepository _duplicateRepository = null!;
    private ScanSession _session = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sm_bench_{Guid.NewGuid():N}.db");
        _db = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _scanRepository = new ScanRepository(_db);
        _duplicateRepository = new DuplicateRepository(_db);
        _session = await _scanRepository.CreateSessionAsync(@"C:\bench");

        var entries = Enumerable.Range(0, 10_000)
            .Select(i => new FileEntry
            {
                Id = 0,
                SessionId = _session.Id,
                FullPath = $@"C:\bench\file-{i:D5}.bin",
                FileName = $"file-{i:D5}.bin",
                Extension = ".bin",
                SizeBytes = 1024 + (i % 100),
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                AccessedUtc = DateTime.UtcNow,
                Attributes = FileAttributes.Normal,
                Category = FileTypeCategory.Unknown,
                IsReparsePoint = false,
            })
            .ToList();

        await _scanRepository.InsertFileEntriesAsync(entries);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Benchmark]
    public Task<IReadOnlyList<DuplicateCandidate>> CandidateQuery_SameSizeBuckets() =>
        _duplicateRepository.GetCandidatesAsync(new DuplicateCandidateQuery
        {
            SessionId = _session.Id,
            MinimumSizeBytes = 0,
            RequireSameSizeBucket = true,
            Extensions = [".bin"],
        });
}
