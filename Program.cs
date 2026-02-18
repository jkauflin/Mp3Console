/*==============================================================================
 * (C) Copyright 2026 John J Kauflin, All rights reserved.
 *----------------------------------------------------------------------------
 * DESCRIPTION:  Console application to connect to Spotify, get a user's playlists,
 *               save information in Azure Cosmos DB, and update MP3 tags 
 *----------------------------------------------------------------------------
 * Modification History
 * 2026-01-14 JJK   Initial version, added Spotify API integration and token management
 * 2026-01-18 JJK   Updated to write to Cosmos DB
 *============================================================================*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using TagLib;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using File = System.IO.File;

namespace Mp3Console
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // Load Spotify credentials from user secrets
            var config = new ConfigurationBuilder()
                .AddUserSecrets<Program>()
                .Build();

            var clientId = config["SpotifyClientId"];
            var clientSecret = config["SpotifyClientSecret"]; // optional for PKCE but useful for refresh on some flows
            var redirectUri = "http://127.0.0.1:5000/callback";
            var nl = Environment.NewLine;

            // Attempt to load stored tokens
            var tokenStore = new TokenStore();
            var stored = tokenStore.Load();

            SpotifyClient spotify = null;
            PKCETokenResponse tokenResponse = null;

            var oauth = new OAuthClient();

            if (stored != null)
            {
                // If token not expired, use it. If expired and refresh token available, refresh.
                if (stored.Expiry > DateTimeOffset.UtcNow && !string.IsNullOrEmpty(stored.AccessToken))
                {
                    spotify = new SpotifyClient(stored.AccessToken);
                    Console.WriteLine("Loaded access token from storage.");
                }
                else if (!string.IsNullOrEmpty(stored.RefreshToken) && !string.IsNullOrEmpty(clientSecret))
                {
                    try
                    {
                        var refreshResp = await oauth.RequestToken(new AuthorizationCodeRefreshRequest(clientId, clientSecret, stored.RefreshToken));
                        tokenResponse = new PKCETokenResponse
                        {
                            AccessToken = refreshResp.AccessToken,
                            ExpiresIn = refreshResp.ExpiresIn,
                            RefreshToken = refreshResp.RefreshToken ?? stored.RefreshToken
                        };

                        spotify = new SpotifyClient(tokenResponse.AccessToken);
                        tokenStore.Save(tokenResponse);
                        Console.WriteLine("Refreshed access token using stored refresh token.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Refresh failed: " + ex.Message);
                    }
                }
            }

            if (spotify == null)
            {
                // Start PKCE OAuth flow
                var codeVerifier = GenerateCodeVerifier();
                var codeChallenge = CreateCodeChallenge(codeVerifier);

                var loginRequest = new LoginRequest(
                    new Uri(redirectUri),
                    clientId,
                    LoginRequest.ResponseType.Code)
                {
                    Scope = new[] {
                        Scopes.PlaylistReadPrivate,
                        Scopes.PlaylistReadCollaborative,
                        Scopes.UserReadPrivate
                    },
                    CodeChallengeMethod = "S256",
                    CodeChallenge = codeChallenge
                };

                var loginUri = loginRequest.ToUri();

                Console.WriteLine("Opening browser to authenticate with Spotify...");
                OpenBrowser(loginUri.ToString());

                // Start a simple local HTTP listener to receive the OAuth callback
                var code = await WaitForCodeAsync(new Uri(redirectUri));
                if (string.IsNullOrEmpty(code))
                {
                    Console.WriteLine("Authorization code not received.");
                    return;
                }

                // Exchange the code + verifier for tokens
                try
                {
                    tokenResponse = await oauth.RequestToken(new PKCETokenRequest(clientId, code, new Uri(redirectUri), codeVerifier));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Token exchange failed: " + ex.Message);
                    return;
                }

                // Create SpotifyClient with access token
                spotify = new SpotifyClient(tokenResponse.AccessToken);
                tokenStore.Save(tokenResponse);
            }

            
            // Get a paginated list of the current user's playlists (first page)
            var playlistPage = await spotify.Playlists.CurrentUsers();

            // Use Paginate to automatically fetch all user playlists
            int cnt = 0;
            string playlistId = "";
            //string targetPlaylistName = "Playlist123";
            string targetPlaylistName = "xxx";
            string rootDir = "C:/Users/johnk/Downloads";
            string pl = "";
            await foreach (var playlist in spotify.Paginate(playlistPage))
            {
                if (targetPlaylistName.Equals(playlist.Name, StringComparison.OrdinalIgnoreCase))
                {
                    playlistId = playlist.Id;
                    break;
                }

                cnt++;
                pl += $"{cnt},{playlist.Id},{playlist.Name}{nl}";

                //playlist.Description
                Console.WriteLine($"{cnt},{playlist.Id},{playlist.Name}");
                await Task.Delay(3000);
            }

            if (cnt > 0)
            {
                string plPath = Path.Combine(rootDir, "splaylists.txt");
                File.WriteAllText(plPath, pl);
            }


            // Check if we found the target playlist
            if (string.IsNullOrEmpty(playlistId))
            {
                Console.WriteLine($"Playlist '{targetPlaylistName}' not found in your library.");
                return;
            }


            var playlistItems = await spotify.Playlists.GetPlaylistItems(playlistId);

            // 2. Use Paginate to iterate through all pages of tracks automatically
            string mp3Name = "";
            string musicDir = "C:/Users/johnk/Music/Audacity/MP3export";
            if (!Directory.Exists(musicDir))
            {
                // report error and exit if music directory doesn't exist
                Console.WriteLine("Music directory not found: "  + musicDir);
                return;
            }

            string m3u = "# Created on " + DateTime.Now.ToString();

            int trackIndex = 0;
            await foreach (var item in spotify.Paginate(playlistItems))
            {
                trackIndex++;
                // Items in a playlist can be tracks or episodes (IPlayableItem)
                if (item.Item is SpotifyAPI.Web.FullTrack track)
                {
                    string fullDate = track.Album.ReleaseDate;

                    // Extract the year (first 4 characters)
                    string year = !string.IsNullOrEmpty(fullDate) && fullDate.Length >= 4
                                  ? fullDate.Substring(0, 4)
                                  : "";

                    // 1. Get the ID of the first artist on the track
                    var artistId = track.Artists[0].Id;
                    // 2. Fetch the Full Artist object to see their genres
                    var fullArtist = await spotify.Artists.Get(artistId);
                    // 3. Access the genres list
                    List<string> genres = fullArtist.Genres;

                    string mainGenre = "";
                    if (fullArtist.Genres != null && fullArtist.Genres.Any())
                    {
                        mainGenre = fullArtist.Genres[0];
                    }
                    else
                    {
                        mainGenre = "Unknown"; // Fallback for empty/null results
                    }

                    // track.Type = "Track"
                    /*
                    string albumImageUrl = track.Album.Images.FirstOrDefault()?.Url;
                    Console.WriteLine($"TrackId: {track.Id}");
                    Console.WriteLine($"AlbumId: {track.Album.Id}");
                    Console.WriteLine($"ArtistId: {track.Artists[0].Id}");
                    Console.WriteLine($"TrackId: {track.Id}");
                    Console.WriteLine($"Genre: {mainGenre}");
                    Console.WriteLine($"Artist: {track.Artists[0].Name}");
                    Console.WriteLine($"Year: {year}");
                    Console.WriteLine($"Album: {track.Album.Name}");
                    Console.WriteLine($"TrackNumber: {track.TrackNumber}");
                    Console.WriteLine($"Song: {track.Name}");
                    */
                    mp3Name = $"{track.Artists[0].Name} - ({year}) {track.Album.Name} - {track.TrackNumber} - {track.Name}.mp3";
                    Console.WriteLine($"*** MP3 name: {mp3Name}");

                    // Attempt to update MP3 tags and rename file based on track number
                    try
                    {
                        //var trackNumber = track.TrackNumber;
                        if (trackIndex > 0)
                        {
                            var pattern = $"{trackIndex:D2}-*.mp3";
                            var matches = Directory.GetFiles(musicDir, pattern, SearchOption.TopDirectoryOnly);
                            if (matches.Length > 0)
                            {
                                var mp3Path = matches[0];
                                var tfile = global::TagLib.File.Create(mp3Path);
                                tfile.Tag.Title = track.Name;
                                tfile.Tag.Album = track.Album?.Name ?? tfile.Tag.Album;
                                tfile.Tag.Performers = track.Artists?.Select(a => a.Name).ToArray() ?? tfile.Tag.Performers;
                                tfile.Tag.Track = (uint)track.TrackNumber;
                                tfile.Tag.Year = uint.TryParse(year, out var y) ? y : tfile.Tag.Year;
                                tfile.Save();

                                //var newName = $"{trackNumber:D2}-{SanitizeFileName(track.Name)}.mp3";
                                var newName = mp3Name;
                                var newPath = Path.Combine(Path.GetDirectoryName(mp3Path) ?? musicDir, newName);
                                if (!File.Exists(newPath))
                                {
                                    File.Move(mp3Path, newPath);
                                    Console.WriteLine($"Tagged and renamed: {Path.GetFileName(mp3Path)} -> {newName}");
                                }
                                else
                                {
                                    Console.WriteLine($"Target filename already exists: {newName}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Tagging/rename error: " + ex.Message);
                    }

                    m3u += nl + mp3Name;
                }
            }

            if (trackIndex > 0) {
                string m3uPath = Path.Combine(musicDir, targetPlaylistName + ".m3u");
                File.WriteAllText(m3uPath, m3u);
            }

            // Display token info (optional)
            Console.WriteLine();
            if (tokenResponse != null)
            {
                Console.WriteLine("Access token expires in (seconds): " + tokenResponse.ExpiresIn);
                Console.WriteLine("Refresh token: " + (tokenResponse.RefreshToken ?? "[not returned]"));
            }
        }




        static string GenerateCodeVerifier()
        {
            // Create a high-entropy random string between 43 and 128 characters
            var bytes = new byte[64];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        static string CreateCodeChallenge(string codeVerifier)
        {
            using var sha256 = SHA256.Create();
            var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
            return Base64UrlEncode(challengeBytes);
        }

        static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var ch in name)
            {
                if (!invalid.Contains(ch)) sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        static void OpenBrowser(string url)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch
            {
                Console.WriteLine("Please open the following URL in your browser:");
                Console.WriteLine(url);
            }
        }

        static async Task<string?> WaitForCodeAsync(Uri redirectUri)
        {
            // Expects redirectUri like "http://localhost:5000/callback"
            var prefix = $"{redirectUri.Scheme}://{redirectUri.Host}:{redirectUri.Port}/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            try
            {
                var context = await listener.GetContextAsync(); // wait for incoming request
                var req = context.Request;
                var resp = context.Response;

                // parse query for "code" and optionally "error"
                var query = req.Url?.Query ?? "";
                var qs = System.Web.HttpUtility.ParseQueryString(query);
                var code = qs["code"];
                var error = qs["error"];

                // return a small HTML response to the browser
                var responseString = "<html><body><h2>You can close this window and return to the application.</h2></body></html>";
                var buffer = Encoding.UTF8.GetBytes(responseString);
                resp.ContentLength64 = buffer.Length;
                await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                resp.OutputStream.Close();

                if (!string.IsNullOrEmpty(error))
                {
                    Console.WriteLine("OAuth returned error: " + error);
                    return null;
                }

                return code;
            }
            catch (Exception ex)
            {
                Console.WriteLine("HTTP listener error: " + ex.Message);
                return null;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}

// Simple token storage: encrypts token JSON and writes to %APPDATA%\Mp3Console\tokens.dat
internal class TokenStore
{
    private readonly string _path;

    public TokenStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mp3Console");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "tokens.dat");
    }

    public void Save(PKCETokenResponse token)
    {
        var dto = new StoredToken
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            Expiry = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn)
        };

        var json = JsonSerializer.Serialize(dto);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Use AES encryption for storage on all platforms. IV is prepended to the ciphertext.
        var key = GetAesKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var ms = new MemoryStream();
        // write IV first
        ms.Write(aes.IV, 0, aes.IV.Length);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
        {
            cs.Write(bytes, 0, bytes.Length);
            cs.FlushFinalBlock();
        }

        var protectedBytes = ms.ToArray();
        File.WriteAllBytes(_path, protectedBytes);
    }

    public StoredToken? Load()
    {
        if (!File.Exists(_path))
            return null;

        var protectedBytes = File.ReadAllBytes(_path);
        byte[] bytes;
        try
        {
            // AES decryption: first 16 bytes are IV
            var key = GetAesKey();
            using var ms = new MemoryStream(protectedBytes);
            var iv = new byte[16];
            var read = ms.Read(iv, 0, iv.Length);
            if (read != iv.Length) return null;
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var outMs = new MemoryStream();
            cs.CopyTo(outMs);
            bytes = outMs.ToArray();
        }
        catch
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(bytes);
            var dto = JsonSerializer.Deserialize<StoredToken>(json);
            return dto;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] GetAesKey()
    {
        // Not cryptographically ideal, but sufficient for simple protection on non-Windows
        var seed = (Environment.UserName + "@" + Environment.MachineName).ToLowerInvariant();
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
    }

    public class StoredToken
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset Expiry { get; set; }
    }
}
