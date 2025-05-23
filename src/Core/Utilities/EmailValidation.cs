﻿using System.Text.RegularExpressions;
using MimeKit;

namespace Bit.Core.Utilities;

public static class EmailValidation
{
    public static bool IsValidEmail(this string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            return false;
        }

        try
        {
            var parsedEmailAddress = MailboxAddress.Parse(emailAddress).Address;
            if (parsedEmailAddress != emailAddress)
            {
                return false;
            }
        }
        catch (ParseException)
        {
            return false;
        }

        // The regex below is intended to catch edge cases that are not handled by the general parsing check above.
        // This enforces the following rules:
        // * Requires ASCII only in the local-part (code points 0-127)
        // * Requires an @ symbol
        // * Allows any char in second-level domain name, including unicode and symbols
        // * Requires at least one period (.) separating SLD from TLD
        // * Must end in a letter (including unicode)
        // See the unit tests for examples of what is allowed.
        var emailFormat = @"^[\x00-\x7F]+@.+\.\p{L}+$";
        if (!Regex.IsMatch(emailAddress, emailFormat))
        {
            return false;
        }

        return true;
    }
}
