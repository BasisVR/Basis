using System;
using System.Collections.Generic;

namespace Basis.Integration.Sso
{
    public readonly struct SsoAccessDecision
    {
        public readonly bool Allowed;
        public readonly string Reason;

        private SsoAccessDecision(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        public static SsoAccessDecision Allow() => new SsoAccessDecision(true, null);
        public static SsoAccessDecision Deny(string reason) => new SsoAccessDecision(false, reason);
    }

    /// <summary>
    /// Evaluates the optional org access rules from <see cref="BasisOidcConfig.AccessConfig"/>
    /// against a signed-in user's claims. Empty rules = admit everyone who authenticated.
    /// Group rules are OR (any allowed group admits); claim rules are AND across rules and OR
    /// within a rule's values; when both kinds are present, both must pass.
    /// </summary>
    public static class BasisSsoAccessControl
    {
        public static SsoAccessDecision Evaluate(BasisOidcConfig config, BasisSsoSession session)
        {
            if (config?.Access == null || session == null) return SsoAccessDecision.Allow();

            if (config.HasGroupRestriction)
            {
                if (!AnyGroupMatches(session.Groups, config.Access.AllowedGroups))
                    return SsoAccessDecision.Deny("You are not a member of an allowed group.");
            }

            if (config.HasClaimRestriction)
            {
                foreach (BasisOidcConfig.ClaimRule rule in config.Access.AllowedClaims)
                {
                    if (rule == null || string.IsNullOrEmpty(rule.Claim)) continue;
                    IReadOnlyList<string> userValues = session.GetClaim(rule.Claim);
                    if (!AnyValueMatches(userValues, rule.Values))
                        return SsoAccessDecision.Deny($"Your '{rule.Claim}' claim does not meet the access requirement.");
                }
            }

            return SsoAccessDecision.Allow();
        }

        private static bool AnyGroupMatches(IReadOnlyList<string> userGroups, List<string> allowed)
        {
            if (userGroups == null || allowed == null) return false;
            foreach (string g in userGroups)
                foreach (string a in allowed)
                    if (string.Equals(g, a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool AnyValueMatches(IReadOnlyList<string> userValues, List<string> allowed)
        {
            if (userValues == null || allowed == null || allowed.Count == 0) return false;
            foreach (string v in userValues)
                foreach (string a in allowed)
                    if (string.Equals(v, a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
