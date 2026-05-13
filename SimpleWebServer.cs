using System.Net;
using System.Text;

namespace WebSnapshots;

public static class SimpleWebServer
{
	public static async Task StartAsync(string rootDir, int port)
	{
		rootDir = Path.GetFullPath(rootDir);
		if (!Directory.Exists(rootDir))
		{
			Console.WriteLine($"[SERVE] Directory not found: {rootDir}");
			return;
		}

		var prefix = $"http://localhost:{port}/";
		using var listener = new HttpListener();
		listener.Prefixes.Add(prefix);
		listener.Start();

		Console.WriteLine($"[SERVE] {prefix}");
		Console.WriteLine($"[SERVE] Root: {rootDir}");
		Console.WriteLine("[SERVE] Ctrl+C to stop");

		while (true)
		{
			var ctx = await listener.GetContextAsync();
			_ = Task.Run(() => Handle(ctx, rootDir));
		}
	}

	private static async Task Handle(HttpListenerContext ctx, string rootDir)
	{
		try
		{
			var reqPath = ctx.Request.Url?.AbsolutePath ?? "/";
			reqPath = Uri.UnescapeDataString(reqPath);

			if (reqPath == "/") reqPath = "/index.htm";

			var local = reqPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
			var full = Path.GetFullPath(Path.Combine(rootDir, local));

			if (!full.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
			{
				ctx.Response.StatusCode = 403;
				ctx.Response.Close();
				return;
			}

			if (!File.Exists(full))
			{
				ctx.Response.StatusCode = 404;
				await WriteText(ctx, "Not found");
				return;
			}

			var ext = Path.GetExtension(full).ToLowerInvariant();
			ctx.Response.ContentType = ext switch
			{
				".htm" or ".html" => "text/html; charset=utf-8",
				".css" => "text/css; charset=utf-8",
				".js" => "application/javascript; charset=utf-8",
				".json" => "application/json; charset=utf-8",
				".webp" => "image/webp",
				".png" => "image/png",
				".jpg" or ".jpeg" => "image/jpeg",
				_ => "application/octet-stream"
			};

			var bytes = await File.ReadAllBytesAsync(full);
			ctx.Response.ContentLength64 = bytes.Length;
			await ctx.Response.OutputStream.WriteAsync(bytes);
			ctx.Response.OutputStream.Close();
		}
		catch
		{
			try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
		}
	}

	private static async Task WriteText(HttpListenerContext ctx, string s)
	{
		var bytes = Encoding.UTF8.GetBytes(s);
		ctx.Response.ContentType = "text/plain; charset=utf-8";
		ctx.Response.ContentLength64 = bytes.Length;
		await ctx.Response.OutputStream.WriteAsync(bytes);
		ctx.Response.OutputStream.Close();
	}
}
