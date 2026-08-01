using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SsisLineage.Core
{
    public class GitDeploymentOptions
    {
        public string RepositoryUrl { get; set; } = "";
        public string BranchName { get; set; } = "migration/ssis-to-airflow";
        public string PersonalAccessToken { get; set; } = "";
        public string CommitMessage { get; set; } = "Auto-generated SSIS to MDS Migration";
        public bool CreatePullRequest { get; set; } = true;
    }

    public static class GitIntegrationService
    {
        public static async Task<string> DeployToGitAsync(byte[] projectZipBytes, GitDeploymentOptions options)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SsisMigration_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. Extract ZIP to temp directory
                using (var ms = new MemoryStream(projectZipBytes))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    archive.ExtractToDirectory(tempDir);
                }

                // 2. Initialize Git
                RunCommand("git", "init", tempDir);

                // Configure auth in URL safely for GitHub and GitLab
                var authUrl = options.RepositoryUrl.Replace("https://", $"https://{options.PersonalAccessToken}@");

                RunCommand("git", $"remote add origin {authUrl}", tempDir);
                
                // Checkout new branch
                RunCommand("git", $"checkout -b {options.BranchName}", tempDir);
                
                // Add and commit
                RunCommand("git", "add .", tempDir);
                RunCommand("git", $"-c user.name=\"SSIS Lineage Hub\" -c user.email=\"bot@ssislineage.io\" commit -m \"{options.CommitMessage}\"", tempDir);
                
                // Push to remote (disabling credential helper so it forces the use of our PAT in the URL)
                RunCommand("git", $"-c credential.helper= push -u origin {options.BranchName} --force", tempDir);

                string prUrl = "";
                if (options.CreatePullRequest && options.RepositoryUrl.Contains("github.com"))
                {
                    prUrl = await CreateGitHubPullRequest(options);
                }

                return !string.IsNullOrEmpty(prUrl) ? prUrl : $"Successfully pushed to branch {options.BranchName}";
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    // Clean up temp git repo
                    try 
                    { 
                        // Set attributes to normal before delete (git creates readonly files)
                        foreach (var file in Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories))
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }
                        Directory.Delete(tempDir, true); 
                    } 
                    catch { }
                }
            }
        }

        private static void RunCommand(string command, string arguments, string workingDirectory)
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) throw new Exception($"Failed to start process {command}");
            
            process.WaitForExit();
            
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                // Mask the PAT in the error output just in case
                var safeError = System.Text.RegularExpressions.Regex.Replace(error, "https://(.*)@", "https://***@");
                throw new Exception($"Git command failed.\nError: {safeError}");
            }
        }

        private static async Task<string> CreateGitHubPullRequest(GitDeploymentOptions options)
        {
            // Extract owner and repo from URL, assuming https://github.com/owner/repo.git
            var urlParts = options.RepositoryUrl.TrimEnd('/').Replace(".git", "").Split('/');
            if (urlParts.Length < 2) return "";
            var repo = urlParts[^1];
            var owner = urlParts[^2];

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.PersonalAccessToken);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SsisLineageHub", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            var body = new
            {
                title = "🚀 SSIS to Modern Data Stack Migration",
                body = "This PR contains auto-generated Airflow DAGs, dbt models, and Great Expectations checks migrated from legacy SSIS packages by SSIS Lineage Hub.\n\n### Migration Artifacts\n- **Airflow DAGs** (`dags/`)\n- **Python Extraction Scripts** (`dags/scripts/`)\n- **dbt Models** (`dags/dbt_project/models/`)",
                head = options.BranchName,
                @base = "main" // Defaulting to main, ideally should query repo's default branch
            };

            var jsonBody = JsonSerializer.Serialize(body);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"https://api.github.com/repos/{owner}/{repo}/pulls", content);
            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                return doc.RootElement.GetProperty("html_url").GetString() ?? "";
            }
            
            var errorBody = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity && errorBody.Contains("A pull request already exists"))
            {
                return $"Branch '{options.BranchName}' updated successfully! (Pull Request already exists)";
            }

            return $"Branch pushed, but failed to create PR: {response.StatusCode} - {errorBody}";
        }
    }
}
