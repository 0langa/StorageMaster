using System.Diagnostics;
using System.Text.Json;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Platform.Windows;

public sealed class InstallerTrustVerifier : IInstallerTrustVerifier
{
    public async Task<InstallerTrustVerificationResult> VerifyAsync(
        string installerPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(installerPath))
        {
            return new InstallerTrustVerificationResult
            {
                IsSigned = false,
                IsSignatureValid = false,
                HasTrustedTimestamp = false,
                Status = "Missing",
                Message = "Installer file does not exist.",
            };
        }

        var escapedPath = installerPath.Replace("'", "''", StringComparison.Ordinal);
        var command = "$sig = Get-AuthenticodeSignature -FilePath '" + escapedPath + "'; " +
                      "[pscustomobject]@{ " +
                      "Status = [string]$sig.Status; " +
                      "StatusMessage = [string]$sig.StatusMessage; " +
                      "IsSigned = $null -ne $sig.SignerCertificate; " +
                      "HasTimestamp = $null -ne $sig.TimeStamperCertificate " +
                      "} | ConvertTo-Json -Compress";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new InstallerTrustVerificationResult
            {
                IsSigned = false,
                IsSignatureValid = false,
                HasTrustedTimestamp = false,
                Status = "UnknownError",
                Message = "Unable to start PowerShell for signature verification.",
            };
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new InstallerTrustVerificationResult
            {
                IsSigned = false,
                IsSignatureValid = false,
                HasTrustedTimestamp = false,
                Status = "UnknownError",
                Message = string.IsNullOrWhiteSpace(stderr)
                    ? "Failed to evaluate installer signature."
                    : stderr.Trim(),
            };
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SignatureProbeResult>(stdout);
            if (dto is null)
                throw new JsonException("Empty signature probe payload.");

            var valid = string.Equals(dto.Status, "Valid", StringComparison.OrdinalIgnoreCase);
            return new InstallerTrustVerificationResult
            {
                IsSigned = dto.IsSigned,
                IsSignatureValid = dto.IsSigned && valid,
                HasTrustedTimestamp = dto.IsSigned && dto.HasTimestamp,
                Status = dto.Status ?? string.Empty,
                Message = dto.StatusMessage ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            return new InstallerTrustVerificationResult
            {
                IsSigned = false,
                IsSignatureValid = false,
                HasTrustedTimestamp = false,
                Status = "UnknownError",
                Message = $"Could not parse signature verification output: {ex.Message}",
            };
        }
    }

    private sealed record SignatureProbeResult(
        string? Status,
        string? StatusMessage,
        bool IsSigned,
        bool HasTimestamp);
}
