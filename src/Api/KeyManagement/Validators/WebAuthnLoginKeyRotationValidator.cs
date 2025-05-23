﻿using Bit.Api.Auth.Models.Request.WebAuthn;
using Bit.Core.Auth.Models.Data;
using Bit.Core.Auth.Repositories;
using Bit.Core.Entities;
using Bit.Core.Exceptions;

namespace Bit.Api.KeyManagement.Validators;

public class WebAuthnLoginKeyRotationValidator : IRotationValidator<IEnumerable<WebAuthnLoginRotateKeyRequestModel>, IEnumerable<WebAuthnLoginRotateKeyData>>
{
    private readonly IWebAuthnCredentialRepository _webAuthnCredentialRepository;

    public WebAuthnLoginKeyRotationValidator(IWebAuthnCredentialRepository webAuthnCredentialRepository)
    {
        _webAuthnCredentialRepository = webAuthnCredentialRepository;
    }

    public async Task<IEnumerable<WebAuthnLoginRotateKeyData>> ValidateAsync(User user, IEnumerable<WebAuthnLoginRotateKeyRequestModel> keysToRotate)
    {
        var result = new List<WebAuthnLoginRotateKeyData>();
        var existing = await _webAuthnCredentialRepository.GetManyByUserIdAsync(user.Id);
        if (existing == null)
        {
            return result;
        }

        var validCredentials = existing.Where(credential => credential.SupportsPrf);
        if (!validCredentials.Any())
        {
            return result;
        }

        foreach (var ea in validCredentials)
        {
            var keyToRotate = keysToRotate.FirstOrDefault(c => c.Id == ea.Id);
            if (keyToRotate == null)
            {
                throw new BadRequestException("All existing webauthn prf keys must be included in the rotation.");
            }

            if (keyToRotate.EncryptedUserKey == null)
            {
                throw new BadRequestException("WebAuthn prf keys must have user-key during rotation.");
            }
            if (keyToRotate.EncryptedPublicKey == null)
            {
                throw new BadRequestException("WebAuthn prf keys must have public-key during rotation.");
            }

            result.Add(keyToRotate.ToWebAuthnRotateKeyData());
        }

        return result;
    }
}
