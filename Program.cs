using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

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

            var playlistPage = await spotify.Playlists.CurrentUsers();

            // 3. Handle Pagination (Optional but Recommended)
            // Use Paginate to automatically fetch all playlists if the user has more than 50
            int cnt = 0;
            string playlistId = "";
            await foreach (var playlist in spotify.Paginate(playlistPage))
            {
                cnt++;
                if (cnt == 1)
                {
                    Console.WriteLine($"{cnt} Name: {playlist.Name}, ID: {playlist.Id}");
                    playlistId = playlist.Id;
                }

                //Console.WriteLine($"Total Tracks: {playlist.Tracks.Total}");
            }

            /*
            Console.WriteLine();
            Console.WriteLine("Your playlists:");
            for (int i = 0; i < allPlaylists.Count; i++)
            {
                var p = allPlaylists[i];
                //Console.WriteLine($"[{i+1}] {p.Name}  (id: {p.Id})");
            }

            Console.WriteLine();
            Console.Write("Enter playlist number to list tracks (or empty to quit): ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                return;

            if (!int.TryParse(input, out var sel) || sel < 1 || sel > allPlaylists.Count)
            {
                Console.WriteLine("Invalid selection.");
                return;
            }

            var playlistId = allPlaylists[sel - 1].Id;
            */


            // 1. Get the initial paging object for the playlist items
            //var playlistItems = await spotify.Playlists.GetItems("YOUR_PLAYLIST_ID");
            var playlistItems = await spotify.Playlists.GetPlaylistItems(playlistId);

            // 2. Use Paginate to iterate through all pages of tracks automatically
            await foreach (var item in spotify.Paginate(playlistItems))
            {
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
                    string mp3Name = $"{track.Artists[0].Name} - ({year}) {track.Album.Name} - {track.TrackNumber} - {track.Name}.mp3";
                    Console.WriteLine($"*** MP3 name: {mp3Name}");
                    Console.WriteLine("-----------------------------");
                }
            }

            /*
1 Name: Playlist123, ID: 6hO0h1g4aFx7JjUKOdrUZX

Artist: Aimee Mann
Year: 1995
Album: I'm With Stupid
TrackNumber: 10
Song: That's Just What You Are
-----------------------------
Genre: Unknown
Artist: Wet Leg
Year: 2025
Album: moisturizer
TrackNumber: 8
Song: pokemon
-----------------------------
Genre: Unknown
Artist: The Hollies
Year: 1970
Album: Confessions of the Mind (Expanded Edition)
TrackNumber: 20
Song: He Ain't Heavy He's My Brother - 2003 Remaster
-----------------------------
            */

            // Get playlist items (auto-paging)
            /*
            var allItems = new List<PlaylistTrack<IPlayableItem>>();
            var playlistItemsPage = await spotify.Playlists.GetItems(playlistId, new PlaylistGetItemsRequest { Limit = 100 });
            allItems.AddRange(playlistItemsPage.Items);
            while (playlistItemsPage.Next != null)
            {
                playlistItemsPage = await spotify.Next(playlistItemsPage);
                allItems.AddRange(playlistItemsPage.Items);
            }

            Console.WriteLine();
            Console.WriteLine($"Tracks in playlist {allPlaylists[sel-1].Name}:");
            int idx = 1;
            foreach (var item in allItems)
            {
                if (item.Track is FullTrack track)
                {
                    var artists = string.Join(", ", track.Artists?.ConvertAll(a => a.Name) ?? new string[] { });
                    Console.WriteLine($"{idx++}: {track.Name} - {artists}");
                }
                else if (item.Track is SimpleTrack simple)
                {
                    Console.WriteLine($"{idx++}: {simple.Name}");
                }
                else
                {
                    Console.WriteLine($"{idx++}: [unknown track type]");
                }
            }
            */

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
