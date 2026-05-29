// Original file provided by Longwelwind (https://github.com/Longwelwind)
// as a part of the RimWorld mod Phi (https://github.com/Longwelwind/Phi)

using System;
using System.Collections.Generic;
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
		/// </summary>
		private static readonly Dictionary<string, Regex> regexCache = new Dictionary<string, Regex>();

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
					string pattern = @"<\/?" + tag + @"(=[\w#]+)?>";
					regex = new Regex(pattern, RegexOptions.Compiled);
					regexCache[tag] = regex;
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
