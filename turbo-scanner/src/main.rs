/// StorageMaster Turbo Scanner
///
/// A high-performance parallel file system enumerator built on jwalk.
/// jwalk uses a work-stealing thread pool to walk directory trees in parallel,
/// which is significantly faster than sequential .NET enumeration on multi-core
/// systems and fast storage (NVMe / SSDs).
///
/// Output: one JSON object per line (JSONL) on stdout.
/// Format: {"path":"...","size":N,"modified_unix":N,"created_unix":N,"is_dir":false}
///
/// Errors (access denied, I/O failures) are written to stderr as plain text
/// and do not abort the scan.

use clap::Parser;
use jwalk::{Parallelism, WalkDir};
use serde::Serialize;
use std::io::{self, BufWriter, Write};
use std::time::UNIX_EPOCH;

#[derive(Parser, Debug)]
#[command(name = "turbo-scanner", about = "StorageMaster Turbo Scanner")]
struct Args {
    /// Root directory to scan
    #[arg(short, long)]
    path: String,

    /// Number of parallel threads (0 = number of logical CPU cores)
    #[arg(short, long, default_value_t = 0)]
    threads: usize,

    /// Minimum file size in bytes to report (0 = all files)
    #[arg(long, default_value_t = 0)]
    min_size: u64,

    /// Skip hidden files and directories (names starting with '.')
    #[arg(long, default_value_t = false)]
    skip_hidden: bool,
}

#[derive(Serialize)]
struct FileRecord<'a> {
    path:          &'a str,
    size:          u64,
    modified_unix: i64,
    created_unix:  i64,
    is_dir:        bool,
}

fn main() {
    let args = Args::parse();

    // 0 threads → use all logical cores via jwalk's default
    let num_threads = if args.threads == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        args.threads
    };

    let stdout = io::stdout();
    let mut writer = BufWriter::with_capacity(256 * 1024, stdout.lock());

    let parallelism = if num_threads <= 1 {
        Parallelism::Serial
    } else {
        Parallelism::RayonNewPool(num_threads)
    };

    for entry in WalkDir::new(&args.path)
        .parallelism(parallelism)
        .skip_hidden(args.skip_hidden)
        .into_iter()
    {
        let entry = match entry {
            Ok(e) => e,
            Err(e) => {
                eprintln!("WARN: {e}");
                continue;
            }
        };

        let metadata = match entry.metadata() {
            Ok(m) => m,
            Err(e) => {
                eprintln!("WARN: {} — {e}", entry.path().display());
                continue;
            }
        };

        let is_dir = metadata.is_dir();
        let size   = if is_dir { 0 } else { metadata.len() };

        if !is_dir && size < args.min_size {
            continue;
        }

        let modified_unix = metadata
            .modified()
            .ok()
            .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
            .map(|d| d.as_secs() as i64)
            .unwrap_or(0);

        let created_unix = metadata
            .created()
            .ok()
            .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
            .map(|d| d.as_secs() as i64)
            .unwrap_or(0);

        let path_str = entry.path().to_string_lossy();
        let record   = FileRecord {
            path: path_str.as_ref(),
            size,
            modified_unix,
            created_unix,
            is_dir,
        };

        // Inline serialisation avoids intermediate allocations.
        if let Ok(json) = serde_json::to_string(&record) {
            let _ = writer.write_all(json.as_bytes());
            let _ = writer.write_all(b"\n");
        }
    }

    // Flush remaining buffered output before exit.
    let _ = writer.flush();
}
