// The Omega versions this program has been verified with (a short test run compared with MARS): Reader Control 6.20 and
// 6.40 on FLUOstar Omega. Any other version is read just the same, but said: the problem "unverified-version" (a
// warning that can be closed) and, in GET /api/health, a check per installation. When its live data cannot be read at
// all the card says so plainly instead of staying "idle" (UnsupportedText).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SAAControl.Bmg
{
    public static class OmegaVersions
    {
        /// <summary>Verified versions (major.minor of the control program's file version).</summary>
        public static readonly string[] Verified = { "6.20", "6.40" };

        public static string VerifiedText => string.Join(", ", Verified);

        /// <summary>"6.40.101.9" -> "6.40"; "" when not known.</summary>
        public static string MajorMinor(string version)
        {
            string[] parts = (version ?? "").Trim().Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)) return "";
            return major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString("00", CultureInfo.InvariantCulture);
        }

        public static bool IsVerified(string version) => Array.IndexOf(Verified, MajorMinor(version)) >= 0;

        static string Shown(string version) => string.IsNullOrWhiteSpace(version) ? "?" : version.Trim();

        /// <summary>The problem's words for an installation of a version not verified: (en, zh).</summary>
        public static (string En, string Zh) NotVerifiedText(string version)
        {
            string v = Shown(version);
            return ("This Omega version (" + v + ") has not been verified with Plate Kinetics Live (verified: " + VerifiedText + "). " +
                    "Before formal experiments, run a short test of 2-3 cycles and compare the data with MARS.",
                    // "\u8fd9\u4e2a Omega \u7248\u672c\uff08<v>\uff09\u8fd8\u6ca1\u6709\u548c Plate Kinetics Live \u4e00\u8d77\u9a8c\u8bc1\u8fc7\uff08\u5df2\u9a8c\u8bc1\uff1a<list>\uff09\u3002\u7528\u4e8e\u6b63\u5f0f\u5b9e\u9a8c\u524d\uff0c\u8bf7\u5148\u8dd1\u4e00\u4e2a 2-3 \u8f6e\u7684\u77ed\u6d4b\u8bd5\uff0c\u5e76\u548c MARS \u91cc\u7684\u6570\u636e\u6838\u5bf9\u3002"
                    "\u8fd9\u4e2a Omega \u7248\u672c\uff08" + v + "\uff09\u8fd8\u6ca1\u6709\u548c Plate Kinetics Live \u4e00\u8d77\u9a8c\u8bc1\u8fc7\uff08\u5df2\u9a8c\u8bc1\uff1a" + VerifiedText +
                    "\uff09\u3002\u7528\u4e8e\u6b63\u5f0f\u5b9e\u9a8c\u524d\uff0c\u8bf7\u5148\u8dd1\u4e00\u4e2a 2-3 \u8f6e\u7684\u77ed\u6d4b\u8bd5\uff0c\u5e76\u548c MARS \u91cc\u7684\u6570\u636e\u6838\u5bf9\u3002");
        }

        /// <summary>The card's words when the live data of an installation cannot be read at all: (en, zh).</summary>
        public static (string En, string Zh) UnsupportedText(string version, bool verified)
        {
            string v = Shown(version);
            string en = "Plate Kinetics Live cannot read the live data of this Omega version (" + v + "): live curves are off for this reader. " +
                        "Omega and the run are not affected; the data are saved in Omega/MARS as usual.";
            // "Plate Kinetics Live \u8bfb\u4e0d\u4e86\u8fd9\u4e2a\u7248\u672c Omega\uff08<v>\uff09\u7684\u5b9e\u65f6\u6570\u636e\uff0c\u8fd9\u53f0\u4eea\u5668\u6682\u4e0d\u663e\u793a\u5b9e\u65f6\u66f2\u7ebf\u3002Omega \u548c\u6b63\u5728\u8fdb\u884c\u7684\u5b9e\u9a8c\u4e0d\u53d7\u5f71\u54cd\uff0c\u6570\u636e\u7167\u5e38\u4fdd\u5b58\u5728 Omega/MARS \u91cc\u3002"
            string zh = "Plate Kinetics Live \u8bfb\u4e0d\u4e86\u8fd9\u4e2a\u7248\u672c Omega\uff08" + v + "\uff09\u7684\u5b9e\u65f6\u6570\u636e\uff0c\u8fd9\u53f0\u4eea\u5668\u6682\u4e0d\u663e\u793a\u5b9e\u65f6\u66f2\u7ebf\u3002" +
                        "Omega \u548c\u6b63\u5728\u8fdb\u884c\u7684\u5b9e\u9a8c\u4e0d\u53d7\u5f71\u54cd\uff0c\u6570\u636e\u7167\u5e38\u4fdd\u5b58\u5728 Omega/MARS \u91cc\u3002";
            if (!verified)
            {
                en += " This version has not been verified (verified: " + VerifiedText + ").";
                // "\u8fd9\u4e2a\u7248\u672c\u8fd8\u6ca1\u6709\u9a8c\u8bc1\u8fc7\uff08\u5df2\u9a8c\u8bc1\uff1a<list>\uff09\u3002"
                zh += "\u8fd9\u4e2a\u7248\u672c\u8fd8\u6ca1\u6709\u9a8c\u8bc1\u8fc7\uff08\u5df2\u9a8c\u8bc1\uff1a" + VerifiedText + "\uff09\u3002";
            }
            return (en, zh);
        }

        /// <summary>At the start: the problem "unverified-version" for each installation of a version not verified.</summary>
        public static void ShowProblems(IEnumerable<Install> installs)
        {
            foreach (Install install in installs ?? Enumerable.Empty<Install>())
            {
                if (IsVerified(install.Version))
                {
                    Problems.Clear("unverified-version", install.Key);
                    continue;
                }
                var (en, zh) = NotVerifiedText(install.Version);
                Problems.Set("unverified-version", Problems.Warning, en, zh, install.Key, dismissible: true);
                Log.Warning(install.Key + ": Omega " + Shown(install.Version) + " has not been verified (verified: " + VerifiedText + ")");
            }
        }

        /// <summary>GET /api/health: per installation whether its version is verified (the verified versions named).</summary>
        public static IEnumerable<Dictionary<string, object>> Checks(IEnumerable<Install> installs)
        {
            var items = new List<Dictionary<string, object>>();
            var list = (installs ?? Enumerable.Empty<Install>()).ToList();
            if (list.Count == 0)
                items.Add(Health.Check("omega", false, "No Omega installation was found on this PC (verified versions: " + VerifiedText + ").",
                                       // "\u8fd9\u53f0\u7535\u8111\u4e0a\u6ca1\u6709\u627e\u5230 Omega\uff08\u5df2\u9a8c\u8bc1\u7684\u7248\u672c\uff1a<list>\uff09\u3002"
                                       "\u8fd9\u53f0\u7535\u8111\u4e0a\u6ca1\u6709\u627e\u5230 Omega\uff08\u5df2\u9a8c\u8bc1\u7684\u7248\u672c\uff1a" + VerifiedText + "\uff09\u3002"));
            foreach (Install install in list)
            {
                bool ok = IsVerified(install.Version);
                string v = Shown(install.Version);
                items.Add(Health.Check("omega-version", ok,
                    install.Key + (install.Serial.Length > 0 ? " (" + install.Serial + ")" : "") + ": Omega " + v +
                    (ok ? ", a verified version." : ", not verified (verified: " + VerifiedText + ")."),
                    // "<key>\uff1aOmega <v>\uff0c\u5df2\u9a8c\u8bc1\u7684\u7248\u672c\u3002" / "<key>\uff1aOmega <v>\uff0c\u672a\u9a8c\u8bc1\uff08\u5df2\u9a8c\u8bc1\uff1a<list>\uff09\u3002"
                    install.Key + (install.Serial.Length > 0 ? "\uff08" + install.Serial + "\uff09" : "") + "\uff1aOmega " + v +
                    (ok ? "\uff0c\u5df2\u9a8c\u8bc1\u7684\u7248\u672c\u3002" : "\uff0c\u672a\u9a8c\u8bc1\uff08\u5df2\u9a8c\u8bc1\uff1a" + VerifiedText + "\uff09\u3002")));
            }
            return items;
        }
    }
}
