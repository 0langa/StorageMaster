/// StorageMaster Turbo Scanner
///
/// A high-performance parallel file system enumerator built on jwalk.
/// jwalk uses a work-stealing thread pool to walk directory trees in parallel,
/// which is significantly faster than sequential .NET enumeration on multi-core
/// systems and fast storage (NVMe / SSDs).
///
/// Output: one JSON object per line (JSONL) on stdout.
/// Format: {"path":"...","size":N,"modified_unix":N,"created_unix":N,"is_dir":false,"is_hidden":false}
///
/// Contract v2: adds `is_hidden` (Windows Hidden attribute; dot-name on Unix) and
/// attribute-based `--skip-hidden` semantics. With `--skip-hidden`, hidden
/// directories are pruned during enumeration so their contents are never
/// reported — matching the managed scanner's EnumerationOptions.AttributesToSkip
/// behavior. The scan root itself is always walked, like the managed scanner.
///
/// Errors (access denied, I/O failures) are written to stderr as plain text
/// and do not abort the scan.
use clap::Parser;
use jwalk::{Parallelism, WalkDirGeneric};
use serde::Serialize;
use std::io::{self, BufWriter, Write};
use std::path::Path;
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

    /// Skip hidden files and directories (Windows Hidden attribute; dot-names on Unix)
    #[arg(long, default_value_t = false)]
    skip_hidden: bool,
}

#[derive(Serialize)]
struct FileRecord<'a> {
    path: &'a str,
    size: u64,
    modified_unix: i64,
    created_unix: i64,
    is_dir: bool,
    is_hidden: bool,
}

#[cfg(windows)]
fn is_hidden(metadata: &std::fs::Metadata, _path: &Path) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_HIDDEN: u32 = 0x2;
    metadata.file_attributes() & FILE_ATTRIBUTE_HIDDEN != 0
}

#[cfg(not(windows))]
fn is_hidden(_metadata: &std::fs::Metadata, path: &Path) -> bool {
    path.file_name()
        .and_then(|n| n.to_str())
        .is_some_and(|n| n.starts_with('.'))
}

fn scan(args: &Args, out: &mut impl Write) {
    // 0 threads → use all logical cores via jwalk's default
    let num_threads = if args.threads == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        args.threads
    };

    let parallelism = if num_threads <= 1 {
        Parallelism::Serial
    } else {
        Parallelism::RayonNewPool(num_threads)
    };

    let prune_hidden = args.skip_hidden;
    // jwalk's built-in skip_hidden is dot-name based; attribute-based pruning
    // below matches the managed scanner, so the built-in stays off.
    let walker = WalkDirGeneric::<((), ())>::new(&args.path)
        .parallelism(parallelism)
        .skip_hidden(false)
        .process_read_dir(move |_depth, _dir_path, _state, children| {
            if !prune_hidden {
                return;
            }
            // Dropping a hidden directory here prevents descent, so files
            // inside hidden directories are never reported.
            children.retain(|child| match child {
                Ok(entry) => {
                    let path = entry.path();
                    match std::fs::symlink_metadata(&path) {
                        Ok(meta) => !is_hidden(&meta, &path),
                        Err(_) => true, // unreadable entries surface as WARN later
                    }
                }
                Err(_) => true, // keep errors so they are reported downstream
            });
        });

    for entry in walker {
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
        let size = if is_dir { 0 } else { metadata.len() };

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

        let path_buf = entry.path();
        let hidden = is_hidden(&metadata, &path_buf);
        let path_str = path_buf.to_string_lossy();
        let record = FileRecord {
            path: path_str.as_ref(),
            size,
            modified_unix,
            created_unix,
            is_dir,
            is_hidden: hidden,
        };

        // Inline serialisation avoids intermediate allocations.
        if let Ok(json) = serde_json::to_string(&record) {
            let _ = out.write_all(json.as_bytes());
            let _ = out.write_all(b"\n");
        }
    }

    // Flush remaining buffered output before exit.
    let _ = out.flush();
}

fn main() {
    let args = Args::parse();

    let stdout = io::stdout();
    let mut writer = BufWriter::with_capacity(256 * 1024, stdout.lock());
    scan(&args, &mut writer);
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::Parser;

    #[test]
    fn args_use_safe_defaults() {
        let args = Args::try_parse_from(["turbo-scanner", "--path", r"C:\data"]).unwrap();

        assert_eq!(args.path, r"C:\data");
        assert_eq!(args.threads, 0);
        assert_eq!(args.min_size, 0);
        assert!(!args.skip_hidden);
    }

    #[test]
    fn args_accept_explicit_scanner_controls() {
        let args = Args::try_parse_from([
            "turbo-scanner",
            "--path",
            r"C:\data",
            "--threads",
            "6",
            "--min-size",
            "4096",
            "--skip-hidden",
        ])
        .unwrap();

        assert_eq!(args.threads, 6);
        assert_eq!(args.min_size, 4096);
        assert!(args.skip_hidden);
    }

    #[test]
    fn file_record_serializes_to_the_jsonl_contract() {
        let record = FileRecord {
            path: r"C:\data\file.txt",
            size: 42,
            modified_unix: 100,
            created_unix: 90,
            is_dir: false,
            is_hidden: true,
        };

        let value = serde_json::to_value(record).unwrap();

        assert_eq!(value["path"], r"C:\data\file.txt");
        assert_eq!(value["size"], 42);
        assert_eq!(value["modified_unix"], 100);
        assert_eq!(value["created_unix"], 90);
        assert_eq!(value["is_dir"], false);
        assert_eq!(value["is_hidden"], true);
    }

    fn scan_paths(args: &Args) -> Vec<String> {
        let mut buffer = Vec::new();
        scan(args, &mut buffer);
        String::from_utf8(buffer)
            .unwrap()
            .lines()
            .map(|line| {
                serde_json::from_str::<serde_json::Value>(line).unwrap()["path"]
                    .as_str()
                    .unwrap()
                    .to_string()
            })
            .collect()
    }

    #[cfg(windows)]
    fn set_hidden(path: &Path) {
        let status = std::process::Command::new("attrib")
            .arg("+H")
            .arg(path)
            .status()
            .expect("attrib should run");
        assert!(status.success(), "attrib +H should succeed");
    }

    #[cfg(windows)]
    #[test]
    fn skip_hidden_prunes_hidden_files_and_directory_contents() {
        let root = std::env::temp_dir().join(format!("turbo_hidden_{}", std::process::id()));
        let hidden_dir = root.join("hidden-dir");
        std::fs::create_dir_all(&hidden_dir).unwrap();
        std::fs::write(root.join("visible.txt"), b"v").unwrap();
        std::fs::write(root.join("hidden.txt"), b"h").unwrap();
        std::fs::write(hidden_dir.join("inside.txt"), b"i").unwrap();
        set_hidden(&root.join("hidden.txt"));
        set_hidden(&hidden_dir);

        let args = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: true,
        };
        let paths = scan_paths(&args);

        assert!(paths.iter().any(|p| p.ends_with("visible.txt")));
        assert!(!paths.iter().any(|p| p.ends_with("hidden.txt")));
        assert!(!paths.iter().any(|p| p.ends_with("hidden-dir")));
        assert!(!paths.iter().any(|p| p.ends_with("inside.txt")));

        let args_inclusive = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: false,
        };
        let all_paths = scan_paths(&args_inclusive);
        assert!(all_paths.iter().any(|p| p.ends_with("hidden.txt")));
        assert!(all_paths.iter().any(|p| p.ends_with("inside.txt")));

        let _ = std::fs::remove_dir_all(&root);
    }

    #[cfg(windows)]
    #[test]
    fn hidden_attribute_is_reported_in_records() {
        let root = std::env::temp_dir().join(format!("turbo_attr_{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("plain.txt"), b"p").unwrap();
        std::fs::write(root.join("shy.txt"), b"s").unwrap();
        set_hidden(&root.join("shy.txt"));

        let args = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: false,
        };
        let mut buffer = Vec::new();
        scan(&args, &mut buffer);
        let records: Vec<serde_json::Value> = String::from_utf8(buffer)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();

        let plain = records
            .iter()
            .find(|r| r["path"].as_str().unwrap().ends_with("plain.txt"))
            .unwrap();
        let shy = records
            .iter()
            .find(|r| r["path"].as_str().unwrap().ends_with("shy.txt"))
            .unwrap();
        assert_eq!(plain["is_hidden"], false);
        assert_eq!(shy["is_hidden"], true);

        let _ = std::fs::remove_dir_all(&root);
    }
}
