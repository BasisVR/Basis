using System;
using System.Diagnostics;
using System.Linq;
using Xunit;

namespace Basis.Contrib.Auth.DecentralizedIds
{
	public class Base64UrlSafeTests
	{
		[Fact]
		public void TestEncode()
		{
			byte[] bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
			string base64 = "3q2-7w";

			Debug.Assert(
				Base64UrlSafe.Encode(bytes).Equals(base64),
				"base64 encoding did not match expected value"
			);
		}

		[Fact]
		public void TestDecode()
		{
			byte[] bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
			string base64 = "3q2-7w";

			Debug.Assert(
				Base64UrlSafe.Decode(base64).SequenceEqual(bytes),
				"base64 decoding was did not match expected value"
			);
		}
	}
}
