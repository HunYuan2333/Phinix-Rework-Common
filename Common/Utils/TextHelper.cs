// Original file provided by Longwelwind (https://github.com/Longwelwind)
// as a part of the RimWorld mod Phi (https://github.com/Longwelwind/Phi)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Utils
{
	public static class TextHelper
	{
		/// <summary>
		/// Array containing all tags provided by Unity's Rich Text API.
		/// </summary>
		private static readonly string[] strippableTags = {"size", "b", "i", "color"};
		/// <summary>
		/// Array containing undesirable tags that should be filtered out.
		/// </summary>
		private static readonly string[] unsafeTags = {"size"};

		/// <summary>
		/// Precompiled regex cache keyed by tag name to avoid per-call allocation.
		/// Lock-free for reads after construction via pre-population at static init time.
		/// Tags not known at init time are rare (only from modded rich text tags) —
		/// those fall through to a lock-protected slow path.
		/// </summary>
		private static readonly Dictionary<string, Regex> regexCache = new Dictionary<string, Regex>();
		private static readonly object regexCacheLock = new object();

		static TextHelper()
		{
			// Pre-populate cache with all known tags at static init time so the
			// hot stripRichText path is lock-free for all built-in tags.
			foreach (string tag in strippableTags.Concat(unsafeTags))
			{
				string pattern = @"<\/?" + tag + @"(=[\w#]+)?>";
				regexCache[tag] = new Regex(pattern, RegexOptions.Compiled);
			}
		}

		/// <summary>
		/// Strips the given set of tags from the input string.
		/// </summary>
		/// <param name="input">String to strip</param>
		/// <param name="strippedTags">Tags to strip from the input string</param>
		/// <returns>Stripped input</returns>
		private static string stripRichText(string input, params string[] strippedTags)
		{
			foreach (string tag in strippedTags) {
				if (!regexCache.TryGetValue(tag, out Regex regex))
				{
					// 罕见的 miss：未知 tag（如 mod 自定义富文本标签）走锁保护慢路径
					lock (regexCacheLock)
					{
						if (!regexCache.TryGetValue(tag, out regex))
						{
							string pattern = @"<\/?" + tag + @"(=[\w#]+)?>";
							regex = new Regex(pattern, RegexOptions.Compiled);
							regexCache[tag] = regex;
						}
					}
				}

				input = regex.Replace (input, "");
			}

			return input;
		}

		/// <summary>
		/// Returns the given string with all rich text tags removed.
		/// </summary>
		/// <param name="input">String to strip</param>
		/// <returns>Stripped input</returns>
		public static string StripRichText(string input)
		{
			return stripRichText(input, strippableTags);
		}

		/// <summary>
		/// Returns the given string with all undesirable tags removed.
		/// At the moment this only removes the size tag.
		/// </summary>
		/// <param name="input">String to sanitise</param>
		/// <returns>Sanitised input</returns>
		public static string SanitiseRichText(string input)
		{
			return stripRichText(input, unsafeTags);
		}
	}
}
