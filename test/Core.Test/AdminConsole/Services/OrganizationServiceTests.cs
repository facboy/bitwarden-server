﻿using System.Text.Json;
using Bit.Core.AdminConsole.Entities.Provider;
using Bit.Core.AdminConsole.Enums;
using Bit.Core.AdminConsole.Enums.Provider;
using Bit.Core.AdminConsole.OrganizationFeatures.OrganizationUsers.Interfaces;
using Bit.Core.AdminConsole.Repositories;
using Bit.Core.AdminConsole.Services;
using Bit.Core.Auth.Entities;
using Bit.Core.Auth.Models.Business.Tokenables;
using Bit.Core.Auth.Repositories;
using Bit.Core.Auth.UserFeatures.TwoFactorAuth.Interfaces;
using Bit.Core.Billing.Enums;
using Bit.Core.Billing.Pricing;
using Bit.Core.Context;
using Bit.Core.Entities;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.Models.Business;
using Bit.Core.Models.Data;
using Bit.Core.Models.Data.Organizations.OrganizationUsers;
using Bit.Core.Models.Mail;
using Bit.Core.OrganizationFeatures.OrganizationSubscriptions.Interface;
using Bit.Core.Platform.Push;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Bit.Core.Settings;
using Bit.Core.Test.AutoFixture.OrganizationFixtures;
using Bit.Core.Test.AutoFixture.OrganizationUserFixtures;
using Bit.Core.Tokens;
using Bit.Core.Tools.Enums;
using Bit.Core.Tools.Models.Business;
using Bit.Core.Tools.Services;
using Bit.Core.Utilities;
using Bit.Test.Common.AutoFixture;
using Bit.Test.Common.AutoFixture.Attributes;
using Bit.Test.Common.Fakes;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReturnsExtensions;
using Xunit;
using Organization = Bit.Core.AdminConsole.Entities.Organization;
using OrganizationUser = Bit.Core.Entities.OrganizationUser;

namespace Bit.Core.Test.Services;

[SutProviderCustomize]
public class OrganizationServiceTests
{
    private readonly IDataProtectorTokenFactory<OrgUserInviteTokenable> _orgUserInviteTokenDataFactory = new FakeDataProtectorTokenFactory<OrgUserInviteTokenable>();

    [Theory, PaidOrganizationCustomize, BitAutoData]
    public async Task OrgImportCreateNewUsers(SutProvider<OrganizationService> sutProvider, Organization org, List<OrganizationUserUserDetails> existingUsers, List<ImportedOrganizationUser> newUsers)
    {
        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        org.UseDirectory = true;
        org.Seats = 10;
        newUsers.Add(new ImportedOrganizationUser
        {
            Email = existingUsers.First().Email,
            ExternalId = existingUsers.First().ExternalId
        });
        var expectedNewUsersCount = newUsers.Count - 1;

        existingUsers.First().Type = OrganizationUserType.Owner;

        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(org.Id).Returns(org);

        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        organizationUserRepository.GetManyDetailsByOrganizationAsync(org.Id)
            .Returns(existingUsers);
        organizationUserRepository.GetCountByOrganizationIdAsync(org.Id)
            .Returns(existingUsers.Count);
        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(org.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(org.Id).Returns(true);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
            );

        await sutProvider.Sut.ImportAsync(org.Id, null, newUsers, null, false, EventSystemUser.PublicApi);

        await sutProvider.GetDependency<IOrganizationUserRepository>().DidNotReceiveWithAnyArgs()
            .UpsertAsync(default);
        await sutProvider.GetDependency<IOrganizationUserRepository>().Received(1)
            .UpsertManyAsync(Arg.Is<IEnumerable<OrganizationUser>>(users => !users.Any()));
        await sutProvider.GetDependency<IOrganizationUserRepository>().DidNotReceiveWithAnyArgs()
            .CreateAsync(default);

        // Create new users
        await sutProvider.GetDependency<IOrganizationUserRepository>().Received(1)
            .CreateManyAsync(Arg.Is<IEnumerable<OrganizationUser>>(users => users.Count() == expectedNewUsersCount));

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(
                Arg.Is<OrganizationInvitesInfo>(info => info.OrgUserTokenPairs.Count() == expectedNewUsersCount && info.IsFreeOrg == (org.PlanType == PlanType.Free) && info.OrganizationName == org.Name));

        // Send events
        await sutProvider.GetDependency<IEventService>().Received(1)
            .LogOrganizationUserEventsAsync(Arg.Is<IEnumerable<(OrganizationUser, EventType, EventSystemUser, DateTime?)>>(events =>
            events.Count() == expectedNewUsersCount));
        await sutProvider.GetDependency<IReferenceEventService>().Received(1)
            .RaiseEventAsync(Arg.Is<ReferenceEvent>(referenceEvent =>
            referenceEvent.Type == ReferenceEventType.InvitedUsers && referenceEvent.Id == org.Id &&
            referenceEvent.Users == expectedNewUsersCount));
    }

    [Theory, PaidOrganizationCustomize, BitAutoData]
    public async Task OrgImportCreateNewUsersAndMarryExistingUser(SutProvider<OrganizationService> sutProvider, Organization org, List<OrganizationUserUserDetails> existingUsers,
        List<ImportedOrganizationUser> newUsers)
    {
        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        org.UseDirectory = true;
        org.Seats = newUsers.Count + existingUsers.Count + 1;
        var reInvitedUser = existingUsers.First();
        reInvitedUser.ExternalId = null;
        newUsers.Add(new ImportedOrganizationUser
        {
            Email = reInvitedUser.Email,
            ExternalId = reInvitedUser.Email,
        });
        var expectedNewUsersCount = newUsers.Count - 1;

        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(org.Id).Returns(org);
        sutProvider.GetDependency<IOrganizationUserRepository>().GetManyDetailsByOrganizationAsync(org.Id)
            .Returns(existingUsers);
        sutProvider.GetDependency<IOrganizationUserRepository>().GetCountByOrganizationIdAsync(org.Id)
            .Returns(existingUsers.Count);
        sutProvider.GetDependency<IOrganizationUserRepository>().GetByIdAsync(reInvitedUser.Id)
            .Returns(new OrganizationUser { Id = reInvitedUser.Id });

        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();

        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(org.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        var currentContext = sutProvider.GetDependency<ICurrentContext>();
        currentContext.ManageUsers(org.Id).Returns(true);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
            );

        await sutProvider.Sut.ImportAsync(org.Id, null, newUsers, null, false, EventSystemUser.PublicApi);

        await sutProvider.GetDependency<IOrganizationUserRepository>().DidNotReceiveWithAnyArgs()
            .UpsertAsync(default);
        await sutProvider.GetDependency<IOrganizationUserRepository>().DidNotReceiveWithAnyArgs()
            .CreateAsync(default);
        await sutProvider.GetDependency<IOrganizationUserRepository>().DidNotReceiveWithAnyArgs()
            .CreateAsync(default, default);

        // Upserted existing user
        await sutProvider.GetDependency<IOrganizationUserRepository>().Received(1)
            .UpsertManyAsync(Arg.Is<IEnumerable<OrganizationUser>>(users => users.Count() == 1));

        // Created and invited new users
        await sutProvider.GetDependency<IOrganizationUserRepository>().Received(1)
            .CreateManyAsync(Arg.Is<IEnumerable<OrganizationUser>>(users => users.Count() == expectedNewUsersCount));

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                info.OrgUserTokenPairs.Count() == expectedNewUsersCount && info.IsFreeOrg == (org.PlanType == PlanType.Free) && info.OrganizationName == org.Name));

        // Sent events
        await sutProvider.GetDependency<IEventService>().Received(1)
            .LogOrganizationUserEventsAsync(Arg.Is<IEnumerable<(OrganizationUser, EventType, EventSystemUser, DateTime?)>>(events =>
            events.Where(e => e.Item2 == EventType.OrganizationUser_Invited).Count() == expectedNewUsersCount));
        await sutProvider.GetDependency<IReferenceEventService>().Received(1)
            .RaiseEventAsync(Arg.Is<ReferenceEvent>(referenceEvent =>
            referenceEvent.Type == ReferenceEventType.InvitedUsers && referenceEvent.Id == org.Id &&
            referenceEvent.Users == expectedNewUsersCount));
    }

    [Theory, BitAutoData]
    public async Task SignupClientAsync_Succeeds(
        OrganizationSignup signup,
        SutProvider<OrganizationService> sutProvider)
    {
        signup.Plan = PlanType.TeamsMonthly;

        var plan = StaticStore.GetPlan(signup.Plan);

        sutProvider.GetDependency<IPricingClient>().GetPlanOrThrow(signup.Plan).Returns(plan);

        var (organization, _, _) = await sutProvider.Sut.SignupClientAsync(signup);

        await sutProvider.GetDependency<IOrganizationRepository>().Received(1).CreateAsync(Arg.Is<Organization>(org =>
            org.Id == organization.Id &&
            org.Name == signup.Name &&
            org.Plan == plan.Name &&
            org.PlanType == plan.Type &&
            org.UsePolicies == plan.HasPolicies &&
            org.PublicKey == signup.PublicKey &&
            org.PrivateKey == signup.PrivateKey &&
            org.UseSecretsManager == false));

        await sutProvider.GetDependency<IOrganizationApiKeyRepository>().Received(1)
            .CreateAsync(Arg.Is<OrganizationApiKey>(orgApiKey =>
                orgApiKey.OrganizationId == organization.Id));

        await sutProvider.GetDependency<IApplicationCacheService>().Received(1)
            .UpsertOrganizationAbilityAsync(organization);

        await sutProvider.GetDependency<IOrganizationUserRepository>().DidNotReceiveWithAnyArgs().CreateAsync(default);

        await sutProvider.GetDependency<ICollectionRepository>().Received(1)
            .CreateAsync(Arg.Is<Collection>(c => c.Name == signup.CollectionName && c.OrganizationId == organization.Id), null, null);

        await sutProvider.GetDependency<IReferenceEventService>().Received(1).RaiseEventAsync(Arg.Is<ReferenceEvent>(
            re =>
                re.Type == ReferenceEventType.Signup &&
                re.PlanType == plan.Type));
    }

    [Theory]
    [OrganizationInviteCustomize(InviteeUserType = OrganizationUserType.User,
         InvitorUserType = OrganizationUserType.Owner), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_NoEmails_Throws(Organization organization, OrganizationUser invitor,
        OrganizationUserInvite invite, SutProvider<OrganizationService> sutProvider)
    {
        invite.Emails = null;
        sutProvider.GetDependency<ICurrentContext>().OrganizationOwner(organization.Id).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        await Assert.ThrowsAsync<NotFoundException>(
            () => sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) }));
    }

    [Theory]
    [OrganizationInviteCustomize, OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_DuplicateEmails_PassesWithoutDuplicates(Organization organization, OrganizationUser invitor,
                [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        OrganizationUserInvite invite, SutProvider<OrganizationService> sutProvider)
    {
        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        invite.Emails = invite.Emails.Append(invite.Emails.First());

        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<ICurrentContext>().OrganizationOwner(organization.Id).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        organizationUserRepository.GetManyByOrganizationAsync(organization.Id, OrganizationUserType.Owner)
            .Returns(new[] { owner });

        // Must set guids in order for dictionary of guids to not throw aggregate exceptions
        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
                );


        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) });

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                info.OrgUserTokenPairs.Count() == invite.Emails.Distinct().Count() &&
                info.IsFreeOrg == (organization.PlanType == PlanType.Free) &&
                info.OrganizationName == organization.Name));

    }

    [Theory]
    [OrganizationInviteCustomize, OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_SsoOrgWithNullSsoConfig_Passes(Organization organization, OrganizationUser invitor,
                [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        OrganizationUserInvite invite, SutProvider<OrganizationService> sutProvider)
    {
        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        // Org must be able to use SSO to trigger this proper test case as we currently only call to retrieve
        // an org's SSO config if the org can use SSO
        organization.UseSso = true;

        // Return null for sso config
        sutProvider.GetDependency<ISsoConfigRepository>().GetByOrganizationIdAsync(organization.Id).ReturnsNull();

        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<ICurrentContext>().OrganizationOwner(organization.Id).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        organizationUserRepository.GetManyByOrganizationAsync(organization.Id, OrganizationUserType.Owner)
            .Returns(new[] { owner });

        // Must set guids in order for dictionary of guids to not throw aggregate exceptions
        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
                );



        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) });

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                info.OrgUserTokenPairs.Count() == invite.Emails.Distinct().Count() &&
                info.IsFreeOrg == (organization.PlanType == PlanType.Free) &&
                info.OrganizationName == organization.Name));
    }

    [Theory]
    [OrganizationInviteCustomize, OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_SsoOrgWithNeverEnabledRequireSsoPolicy_Passes(Organization organization, SsoConfig ssoConfig, OrganizationUser invitor,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
OrganizationUserInvite invite, SutProvider<OrganizationService> sutProvider)
    {
        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        // Org must be able to use SSO and policies to trigger this test case
        organization.UseSso = true;
        organization.UsePolicies = true;

        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<ICurrentContext>().OrganizationOwner(organization.Id).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        organizationUserRepository.GetManyByOrganizationAsync(organization.Id, OrganizationUserType.Owner)
            .Returns(new[] { owner });

        ssoConfig.Enabled = true;
        sutProvider.GetDependency<ISsoConfigRepository>().GetByOrganizationIdAsync(organization.Id).Returns(ssoConfig);


        // Return null policy to mimic new org that's never turned on the require sso policy
        sutProvider.GetDependency<IPolicyRepository>().GetManyByOrganizationIdAsync(organization.Id).ReturnsNull();

        // Must set guids in order for dictionary of guids to not throw aggregate exceptions
        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
                );

        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) });

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                info.OrgUserTokenPairs.Count() == invite.Emails.Distinct().Count() &&
                info.IsFreeOrg == (organization.PlanType == PlanType.Free) &&
                info.OrganizationName == organization.Name));
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.Admin,
        InvitorUserType = OrganizationUserType.Owner
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_NoOwner_Throws(Organization organization, OrganizationUser invitor,
        OrganizationUserInvite invite, SutProvider<OrganizationService> sutProvider)
    {
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<ICurrentContext>().OrganizationOwner(organization.Id).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) }));
        Assert.Contains("Organization must have at least one confirmed owner.", exception.Message);
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.Owner,
        InvitorUserType = OrganizationUserType.Admin
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_NonOwnerConfiguringOwner_Throws(Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        currentContext.OrganizationAdmin(organization.Id).Returns(true);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) }));
        Assert.Contains("only an owner", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.Custom,
        InvitorUserType = OrganizationUserType.User
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_NonAdminConfiguringAdmin_Throws(Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        organization.UseCustomPermissions = true;

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        currentContext.OrganizationUser(organization.Id).Returns(true);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) }));
        Assert.Contains("your account does not have permission to manage users", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
         InviteeUserType = OrganizationUserType.Custom,
         InvitorUserType = OrganizationUserType.Admin
     ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_WithCustomType_WhenUseCustomPermissionsIsFalse_Throws(Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        organization.UseCustomPermissions = false;

        invite.Permissions = null;
        invitor.Status = OrganizationUserStatusType.Confirmed;
        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        organizationUserRepository.GetManyByOrganizationAsync(organization.Id, OrganizationUserType.Owner)
            .Returns(new[] { invitor });
        currentContext.OrganizationOwner(organization.Id).Returns(true);
        currentContext.ManageUsers(organization.Id).Returns(true);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) }));
        Assert.Contains("to enable custom permissions", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
         InviteeUserType = OrganizationUserType.Custom,
         InvitorUserType = OrganizationUserType.Admin
     ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_WithCustomType_WhenUseCustomPermissionsIsTrue_Passes(Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        organization.Seats = 10;
        organization.UseCustomPermissions = true;

        invite.Permissions = null;
        invitor.Status = OrganizationUserStatusType.Confirmed;
        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        currentContext.OrganizationOwner(organization.Id).Returns(true);
        currentContext.ManageUsers(organization.Id).Returns(true);

        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) });
    }

    [Theory]
    [BitAutoData(OrganizationUserType.Admin)]
    [BitAutoData(OrganizationUserType.Owner)]
    [BitAutoData(OrganizationUserType.User)]
    public async Task InviteUsers_WithNonCustomType_WhenUseCustomPermissionsIsFalse_Passes(OrganizationUserType inviteUserType, Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        organization.Seats = 10;
        organization.UseCustomPermissions = false;

        invite.Type = inviteUserType;
        invite.Permissions = null;
        invitor.Status = OrganizationUserStatusType.Confirmed;
        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        currentContext.OrganizationOwner(organization.Id).Returns(true);
        currentContext.ManageUsers(organization.Id).Returns(true);

        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) });
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.User,
        InvitorUserType = OrganizationUserType.Custom
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_CustomUserWithoutManageUsersConfiguringUser_Throws(Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        invitor.Permissions = JsonSerializer.Serialize(new Permissions() { ManageUsers = false },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);
        currentContext.OrganizationCustom(organization.Id).Returns(true);
        currentContext.ManageUsers(organization.Id).Returns(false);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) }));
        Assert.Contains("account does not have permission", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.Admin,
        InvitorUserType = OrganizationUserType.Custom
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_CustomUserConfiguringAdmin_Throws(Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        invitor.Permissions = JsonSerializer.Serialize(new Permissions() { ManageUsers = true },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        currentContext.OrganizationCustom(organization.Id).Returns(true);
        currentContext.ManageUsers(organization.Id).Returns(true);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) }));
        Assert.Contains("can not manage admins", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.User,
        InvitorUserType = OrganizationUserType.Owner
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_NoPermissionsObject_Passes(Organization organization, OrganizationUserInvite invite,
        OrganizationUser invitor, SutProvider<OrganizationService> sutProvider)
    {
        invite.Permissions = null;
        invitor.Status = OrganizationUserStatusType.Confirmed;
        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        currentContext.OrganizationOwner(organization.Id).Returns(true);
        currentContext.ManageUsers(organization.Id).Returns(true);

        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, new (OrganizationUserInvite, string)[] { (invite, null) });
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.User,
        InvitorUserType = OrganizationUserType.Custom
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUser_Passes(Organization organization, OrganizationUserInvite invite, string externalId,
        OrganizationUser invitor,
        SutProvider<OrganizationService> sutProvider)
    {
        // This method is only used to invite 1 user at a time
        invite.Emails = new[] { invite.Emails.First() };

        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        InviteUser_ArrangeCurrentContextPermissions(organization, sutProvider);

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
            );

        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);
        SetupOrgUserRepositoryCreateAsyncMock(organizationUserRepository);

        await sutProvider.Sut.InviteUserAsync(organization.Id, invitor.UserId, systemUser: null, invite, externalId);

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                info.OrgUserTokenPairs.Count() == 1 &&
                info.IsFreeOrg == (organization.PlanType == PlanType.Free) &&
                info.OrganizationName == organization.Name));

        await sutProvider.GetDependency<IEventService>().Received(1).LogOrganizationUserEventsAsync(Arg.Any<IEnumerable<(OrganizationUser, EventType, DateTime?)>>());
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.User,
        InvitorUserType = OrganizationUserType.Custom
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUser_InvitingMoreThanOneUser_Throws(Organization organization, OrganizationUserInvite invite, string externalId,
        OrganizationUser invitor,
        SutProvider<OrganizationService> sutProvider)
    {
        var exception = await Assert.ThrowsAsync<BadRequestException>(() => sutProvider.Sut.InviteUserAsync(organization.Id, invitor.UserId, systemUser: null, invite, externalId));
        Assert.Contains("This method can only be used to invite a single user.", exception.Message);

        await sutProvider.GetDependency<IMailService>().DidNotReceiveWithAnyArgs()
            .SendOrganizationInviteEmailsAsync(default);
        await sutProvider.GetDependency<IEventService>().DidNotReceive()
            .LogOrganizationUserEventsAsync(Arg.Any<IEnumerable<(OrganizationUser, EventType, EventSystemUser, DateTime?)>>());
        await sutProvider.GetDependency<IEventService>().DidNotReceive()
            .LogOrganizationUserEventsAsync(Arg.Any<IEnumerable<(OrganizationUser, EventType, DateTime?)>>());
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.User,
        InvitorUserType = OrganizationUserType.Custom
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUser_UserAlreadyInvited_Throws(Organization organization, OrganizationUserInvite invite, string externalId,
        OrganizationUser invitor,
        SutProvider<OrganizationService> sutProvider)
    {
        // This method is only used to invite 1 user at a time
        invite.Emails = new[] { invite.Emails.First() };

        // The user has already been invited
        sutProvider.GetDependency<IOrganizationUserRepository>()
            .SelectKnownEmailsAsync(organization.Id, Arg.Any<IEnumerable<string>>(), false)
            .Returns(new List<string> { invite.Emails.First() });

        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        InviteUser_ArrangeCurrentContextPermissions(organization, sutProvider);

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
            );

        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);
        SetupOrgUserRepositoryCreateAsyncMock(organizationUserRepository);

        var exception = await Assert.ThrowsAsync<BadRequestException>(() => sutProvider.Sut
            .InviteUserAsync(organization.Id, invitor.UserId, systemUser: null, invite, externalId));
        Assert.Contains("This user has already been invited", exception.Message);

        // MailService and EventService are still called, but with no OrgUsers
        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                !info.OrgUserTokenPairs.Any() &&
                info.IsFreeOrg == (organization.PlanType == PlanType.Free) &&
                info.OrganizationName == organization.Name));
        await sutProvider.GetDependency<IEventService>().Received(1)
            .LogOrganizationUserEventsAsync(Arg.Is<IEnumerable<(OrganizationUser, EventType, DateTime?)>>(events => !events.Any()));
    }

    private void InviteUser_ArrangeCurrentContextPermissions(Organization organization, SutProvider<OrganizationService> sutProvider)
    {
        var currentContext = sutProvider.GetDependency<ICurrentContext>();
        currentContext.ManageUsers(organization.Id).Returns(true);
        currentContext.AccessReports(organization.Id).Returns(true);
        currentContext.ManageGroups(organization.Id).Returns(true);
        currentContext.ManagePolicies(organization.Id).Returns(true);
        currentContext.ManageScim(organization.Id).Returns(true);
        currentContext.ManageSso(organization.Id).Returns(true);
        currentContext.AccessEventLogs(organization.Id).Returns(true);
        currentContext.AccessImportExport(organization.Id).Returns(true);
        currentContext.EditAnyCollection(organization.Id).Returns(true);
        currentContext.ManageResetPassword(organization.Id).Returns(true);
        currentContext.GetOrganization(organization.Id)
            .Returns(new CurrentContextOrganization()
            {
                Permissions = new Permissions
                {
                    CreateNewCollections = true,
                    DeleteAnyCollection = true
                }
            });
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.User,
        InvitorUserType = OrganizationUserType.Custom
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_Passes(Organization organization, IEnumerable<(OrganizationUserInvite invite, string externalId)> invites,
        OrganizationUser invitor,
        SutProvider<OrganizationService> sutProvider)
    {
        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        InviteUser_ArrangeCurrentContextPermissions(organization, sutProvider);

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
            );

        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);
        SetupOrgUserRepositoryCreateAsyncMock(organizationUserRepository);

        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitor.UserId, systemUser: null, invites);

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                info.OrgUserTokenPairs.Count() == invites.SelectMany(i => i.invite.Emails).Count() &&
                info.IsFreeOrg == (organization.PlanType == PlanType.Free) &&
                info.OrganizationName == organization.Name));

        await sutProvider.GetDependency<IEventService>().Received(1).LogOrganizationUserEventsAsync(Arg.Any<IEnumerable<(OrganizationUser, EventType, DateTime?)>>());
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.User,
        InvitorUserType = OrganizationUserType.Custom
    ), OrganizationCustomize, BitAutoData]
    public async Task InviteUsers_WithEventSystemUser_Passes(Organization organization, EventSystemUser eventSystemUser, IEnumerable<(OrganizationUserInvite invite, string externalId)> invites,
        OrganizationUser invitor,
        SutProvider<OrganizationService> sutProvider)
    {
        // Setup FakeDataProtectorTokenFactory for creating new tokens - this must come first in order to avoid resetting mocks
        sutProvider.SetDependency(_orgUserInviteTokenDataFactory, "orgUserInviteTokenDataFactory");
        sutProvider.Create();

        invitor.Permissions = JsonSerializer.Serialize(new Permissions() { ManageUsers = true },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        var currentContext = sutProvider.GetDependency<ICurrentContext>();

        organizationRepository.GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);

        SetupOrgUserRepositoryCreateAsyncMock(organizationUserRepository);
        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);

        currentContext.ManageUsers(organization.Id).Returns(true);

        // Mock tokenable factory to return a token that expires in 5 days
        sutProvider.GetDependency<IOrgUserInviteTokenableFactory>()
            .CreateToken(Arg.Any<OrganizationUser>())
            .Returns(
                info => new OrgUserInviteTokenable(info.Arg<OrganizationUser>())
                {
                    ExpirationDate = DateTime.UtcNow.Add(TimeSpan.FromDays(5))
                }
            );

        await sutProvider.Sut.InviteUsersAsync(organization.Id, invitingUserId: null, eventSystemUser, invites);

        await sutProvider.GetDependency<IMailService>().Received(1)
            .SendOrganizationInviteEmailsAsync(Arg.Is<OrganizationInvitesInfo>(info =>
                info.OrgUserTokenPairs.Count() == invites.SelectMany(i => i.invite.Emails).Count() &&
                info.IsFreeOrg == (organization.PlanType == PlanType.Free) &&
                info.OrganizationName == organization.Name));

        await sutProvider.GetDependency<IEventService>().Received(1).LogOrganizationUserEventsAsync(Arg.Any<IEnumerable<(OrganizationUser, EventType, EventSystemUser, DateTime?)>>());
    }

    [Theory, BitAutoData, OrganizationCustomize, OrganizationInviteCustomize]
    public async Task InviteUsers_WithSecretsManager_Passes(Organization organization,
        IEnumerable<(OrganizationUserInvite invite, string externalId)> invites,
        OrganizationUser savingUser, SutProvider<OrganizationService> sutProvider)
    {
        organization.PlanType = PlanType.EnterpriseAnnually;
        InviteUserHelper_ArrangeValidPermissions(organization, savingUser, sutProvider);

        // Set up some invites to grant access to SM
        invites.First().invite.AccessSecretsManager = true;
        var invitedSmUsers = invites.First().invite.Emails.Count();
        foreach (var (invite, externalId) in invites.Skip(1))
        {
            invite.AccessSecretsManager = false;
        }

        // Assume we need to add seats for all invited SM users
        sutProvider.GetDependency<ICountNewSmSeatsRequiredQuery>()
            .CountNewSmSeatsRequiredAsync(organization.Id, invitedSmUsers).Returns(invitedSmUsers);

        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        SetupOrgUserRepositoryCreateManyAsyncMock(organizationUserRepository);
        SetupOrgUserRepositoryCreateAsyncMock(organizationUserRepository);

        sutProvider.GetDependency<IPricingClient>().GetPlanOrThrow(organization.PlanType).Returns(StaticStore.GetPlan(organization.PlanType));

        await sutProvider.Sut.InviteUsersAsync(organization.Id, savingUser.Id, systemUser: null, invites);

        await sutProvider.GetDependency<IUpdateSecretsManagerSubscriptionCommand>().Received(1)
            .UpdateSubscriptionAsync(Arg.Is<SecretsManagerSubscriptionUpdate>(update =>
                update.SmSeats == organization.SmSeats + invitedSmUsers &&
                !update.SmServiceAccountsChanged &&
                !update.MaxAutoscaleSmSeatsChanged &&
                !update.MaxAutoscaleSmSeatsChanged));
    }

    [Theory, BitAutoData, OrganizationCustomize, OrganizationInviteCustomize]
    public async Task InviteUsers_WithSecretsManager_WhenErrorIsThrown_RevertsAutoscaling(Organization organization,
        IEnumerable<(OrganizationUserInvite invite, string externalId)> invites,
        OrganizationUser savingUser, SutProvider<OrganizationService> sutProvider)
    {
        var initialSmSeats = organization.SmSeats;
        InviteUserHelper_ArrangeValidPermissions(organization, savingUser, sutProvider);

        // Set up some invites to grant access to SM
        invites.First().invite.AccessSecretsManager = true;
        var invitedSmUsers = invites.First().invite.Emails.Count();
        foreach (var (invite, externalId) in invites.Skip(1))
        {
            invite.AccessSecretsManager = false;
        }

        // Assume we need to add seats for all invited SM users
        sutProvider.GetDependency<ICountNewSmSeatsRequiredQuery>()
            .CountNewSmSeatsRequiredAsync(organization.Id, invitedSmUsers).Returns(invitedSmUsers);

        // Mock SecretsManagerSubscriptionUpdateCommand to actually change the organization's subscription in memory
        sutProvider.GetDependency<IUpdateSecretsManagerSubscriptionCommand>()
            .UpdateSubscriptionAsync(Arg.Any<SecretsManagerSubscriptionUpdate>())
            .ReturnsForAnyArgs(Task.FromResult(0)).AndDoes(x => organization.SmSeats += invitedSmUsers);

        // Throw error at the end of the try block
        sutProvider.GetDependency<IReferenceEventService>().RaiseEventAsync(default)
            .ThrowsForAnyArgs<BadRequestException>();

        sutProvider.GetDependency<IPricingClient>().GetPlanOrThrow(organization.PlanType)
            .Returns(StaticStore.GetPlan(organization.PlanType));

        await Assert.ThrowsAsync<AggregateException>(async () =>
            await sutProvider.Sut.InviteUsersAsync(organization.Id, savingUser.Id, systemUser: null, invites));

        // OrgUser is reverted
        // Note: we don't know what their guids are so comparing length is the best we can do
        var invitedEmails = invites.SelectMany(i => i.invite.Emails);
        await sutProvider.GetDependency<IOrganizationUserRepository>().Received(1).DeleteManyAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Count() == invitedEmails.Count()));

        Received.InOrder(() =>
        {
            // Initial autoscaling
            sutProvider.GetDependency<IUpdateSecretsManagerSubscriptionCommand>()
                .UpdateSubscriptionAsync(Arg.Is<SecretsManagerSubscriptionUpdate>(update =>
                    update.SmSeats == initialSmSeats + invitedSmUsers &&
                    !update.SmServiceAccountsChanged &&
                    !update.MaxAutoscaleSmSeatsChanged &&
                    !update.MaxAutoscaleSmSeatsChanged));

            // Revert autoscaling
            sutProvider.GetDependency<IUpdateSecretsManagerSubscriptionCommand>()
                .UpdateSubscriptionAsync(Arg.Is<SecretsManagerSubscriptionUpdate>(update =>
                    update.SmSeats == initialSmSeats &&
                    !update.SmServiceAccountsChanged &&
                    !update.MaxAutoscaleSmSeatsChanged &&
                    !update.MaxAutoscaleSmSeatsChanged));
        });
    }

    private void InviteUserHelper_ArrangeValidPermissions(Organization organization, OrganizationUser savingUser,
    SutProvider<OrganizationService> sutProvider)
    {
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<ICurrentContext>().OrganizationOwner(organization.Id).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
    }

    [Theory, BitAutoData]
    public async Task UpdateOrganizationKeysAsync_WithoutManageResetPassword_Throws(Guid orgId, string publicKey,
        string privateKey, SutProvider<OrganizationService> sutProvider)
    {
        var currentContext = Substitute.For<ICurrentContext>();
        currentContext.ManageResetPassword(orgId).Returns(false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sutProvider.Sut.UpdateOrganizationKeysAsync(orgId, publicKey, privateKey));
    }

    [Theory, BitAutoData]
    public async Task UpdateOrganizationKeysAsync_KeysAlreadySet_Throws(Organization org, string publicKey,
        string privateKey, SutProvider<OrganizationService> sutProvider)
    {
        var currentContext = sutProvider.GetDependency<ICurrentContext>();
        currentContext.ManageResetPassword(org.Id).Returns(true);

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        organizationRepository.GetByIdAsync(org.Id).Returns(org);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.UpdateOrganizationKeysAsync(org.Id, publicKey, privateKey));
        Assert.Contains("Organization Keys already exist", exception.Message);
    }

    [Theory, BitAutoData]
    public async Task UpdateOrganizationKeysAsync_KeysAlreadySet_Success(Organization org, string publicKey,
        string privateKey, SutProvider<OrganizationService> sutProvider)
    {
        org.PublicKey = null;
        org.PrivateKey = null;

        var currentContext = sutProvider.GetDependency<ICurrentContext>();
        currentContext.ManageResetPassword(org.Id).Returns(true);

        var organizationRepository = sutProvider.GetDependency<IOrganizationRepository>();
        organizationRepository.GetByIdAsync(org.Id).Returns(org);

        await sutProvider.Sut.UpdateOrganizationKeysAsync(org.Id, publicKey, privateKey);
    }

    [Theory]
    [PaidOrganizationCustomize(CheckedPlanType = PlanType.EnterpriseAnnually)]
    [BitAutoData("Cannot set max seat autoscaling below seat count", 1, 0, 2, 2)]
    [BitAutoData("Cannot set max seat autoscaling below seat count", 4, -1, 6, 6)]
    public async Task Enterprise_UpdateMaxSeatAutoscaling_BadInputThrows(string expectedMessage,
        int? maxAutoscaleSeats, int seatAdjustment, int? currentSeats, int? currentMaxAutoscaleSeats,
        Organization organization, SutProvider<OrganizationService> sutProvider)
        => await UpdateSubscription_BadInputThrows(expectedMessage, maxAutoscaleSeats, seatAdjustment, currentSeats,
            currentMaxAutoscaleSeats, organization, sutProvider);
    [Theory]
    [FreeOrganizationCustomize]
    [BitAutoData("Your plan does not allow seat autoscaling", 10, 0, null, null)]
    public async Task Free_UpdateMaxSeatAutoscaling_BadInputThrows(string expectedMessage,
        int? maxAutoscaleSeats, int seatAdjustment, int? currentSeats, int? currentMaxAutoscaleSeats,
        Organization organization, SutProvider<OrganizationService> sutProvider)
        => await UpdateSubscription_BadInputThrows(expectedMessage, maxAutoscaleSeats, seatAdjustment, currentSeats,
            currentMaxAutoscaleSeats, organization, sutProvider);

    private async Task UpdateSubscription_BadInputThrows(string expectedMessage,
        int? maxAutoscaleSeats, int seatAdjustment, int? currentSeats, int? currentMaxAutoscaleSeats,
        Organization organization, SutProvider<OrganizationService> sutProvider)
    {
        organization.Seats = currentSeats;
        organization.MaxAutoscaleSeats = currentMaxAutoscaleSeats;
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);

        sutProvider.GetDependency<IPricingClient>().GetPlanOrThrow(organization.PlanType)
            .Returns(StaticStore.GetPlan(organization.PlanType));

        var exception = await Assert.ThrowsAsync<BadRequestException>(() => sutProvider.Sut.UpdateSubscription(organization.Id,
            seatAdjustment, maxAutoscaleSeats));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Theory, BitAutoData]
    public async Task UpdateSubscription_NoOrganization_Throws(Guid organizationId, SutProvider<OrganizationService> sutProvider)
    {
        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organizationId).Returns((Organization)null);

        await Assert.ThrowsAsync<NotFoundException>(() => sutProvider.Sut.UpdateSubscription(organizationId, 0, null));
    }

    [Theory, SecretsManagerOrganizationCustomize]
    [BitAutoData("You cannot have more Secrets Manager seats than Password Manager seats.", -1)]
    public async Task UpdateSubscription_PmSeatAdjustmentLessThanSmSeats_Throws(string expectedMessage,
        int seatAdjustment, Organization organization, SutProvider<OrganizationService> sutProvider)
    {
        organization.Seats = 100;
        organization.SmSeats = 100;

        sutProvider.GetDependency<IPricingClient>().GetPlanOrThrow(organization.PlanType)
            .Returns(StaticStore.GetPlan(organization.PlanType));

        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);

        var actual = await Assert.ThrowsAsync<BadRequestException>(() => sutProvider.Sut.UpdateSubscription(organization.Id, seatAdjustment, null));
        Assert.Contains(expectedMessage, actual.Message);
    }

    [Theory, PaidOrganizationCustomize]
    [BitAutoData(0, 100, null, true, "")]
    [BitAutoData(0, 100, 100, true, "")]
    [BitAutoData(0, null, 100, true, "")]
    [BitAutoData(1, 100, null, true, "")]
    [BitAutoData(1, 100, 100, false, "Seat limit has been reached")]
    public async Task CanScaleAsync(int seatsToAdd, int? currentSeats, int? maxAutoscaleSeats,
        bool expectedResult, string expectedFailureMessage, Organization organization,
        SutProvider<OrganizationService> sutProvider)
    {
        organization.Seats = currentSeats;
        organization.MaxAutoscaleSeats = maxAutoscaleSeats;
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
        sutProvider.GetDependency<IProviderRepository>().GetByOrganizationIdAsync(organization.Id).ReturnsNull();

        var (result, failureMessage) = await sutProvider.Sut.CanScaleAsync(organization, seatsToAdd);

        if (expectedFailureMessage == string.Empty)
        {
            Assert.Empty(failureMessage);
        }
        else
        {
            Assert.Contains(expectedFailureMessage, failureMessage);
        }
        Assert.Equal(expectedResult, result);
    }

    [Theory, PaidOrganizationCustomize, BitAutoData]
    public async Task CanScaleAsync_FailsOnSelfHosted(Organization organization,
        SutProvider<OrganizationService> sutProvider)
    {
        sutProvider.GetDependency<IGlobalSettings>().SelfHosted.Returns(true);
        var (result, failureMessage) = await sutProvider.Sut.CanScaleAsync(organization, 10);

        Assert.False(result);
        Assert.Contains("Cannot autoscale on self-hosted instance", failureMessage);
    }

    [Theory, PaidOrganizationCustomize, BitAutoData]
    public async Task CanScaleAsync_FailsOnResellerManagedOrganization(
        Organization organization,
        SutProvider<OrganizationService> sutProvider)
    {
        var provider = new Provider
        {
            Enabled = true,
            Type = ProviderType.Reseller
        };

        sutProvider.GetDependency<IProviderRepository>().GetByOrganizationIdAsync(organization.Id).Returns(provider);

        var (result, failureMessage) = await sutProvider.Sut.CanScaleAsync(organization, 10);

        Assert.False(result);
        Assert.Contains("Seat limit has been reached. Contact your provider to purchase additional seats.", failureMessage);
    }

    private void RestoreRevokeUser_Setup(
        Organization organization,
        OrganizationUser? requestingOrganizationUser,
        OrganizationUser targetOrganizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        if (requestingOrganizationUser != null)
        {
            requestingOrganizationUser.OrganizationId = organization.Id;
        }
        targetOrganizationUser.OrganizationId = organization.Id;

        sutProvider.GetDependency<IOrganizationRepository>().GetByIdAsync(organization.Id).Returns(organization);
        sutProvider.GetDependency<ICurrentContext>().OrganizationOwner(organization.Id).Returns(requestingOrganizationUser != null && requestingOrganizationUser.Type is OrganizationUserType.Owner);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(requestingOrganizationUser != null && (requestingOrganizationUser.Type is OrganizationUserType.Owner or OrganizationUserType.Admin));
        sutProvider.GetDependency<IHasConfirmedOwnersExceptQuery>()
            .HasConfirmedOwnersExceptAsync(organization.Id, Arg.Any<IEnumerable<Guid>>())
            .Returns(true);
    }

    [Theory, BitAutoData]
    public async Task RevokeUser_Success(Organization organization, [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser] OrganizationUser organizationUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        await sutProvider.Sut.RevokeUserAsync(organizationUser, owner.Id);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RevokeAsync(organizationUser.Id);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Revoked);
    }

    [Theory, BitAutoData]
    public async Task RevokeUser_WithPushSyncOrgKeysOnRevokeRestoreEnabled_Success(Organization organization, [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser] OrganizationUser organizationUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IFeatureService>()
            .IsEnabled(FeatureFlagKeys.PushSyncOrgKeysOnRevokeRestore)
            .Returns(true);

        await sutProvider.Sut.RevokeUserAsync(organizationUser, owner.Id);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RevokeAsync(organizationUser.Id);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Revoked);
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(1)
            .PushSyncOrgKeysAsync(organizationUser.UserId!.Value);
    }

    [Theory, BitAutoData]
    public async Task RevokeUser_WithEventSystemUser_Success(Organization organization, [OrganizationUser] OrganizationUser organizationUser, EventSystemUser eventSystemUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, null, organizationUser, sutProvider);

        await sutProvider.Sut.RevokeUserAsync(organizationUser, eventSystemUser);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RevokeAsync(organizationUser.Id);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Revoked, eventSystemUser);
    }

    [Theory, BitAutoData]
    public async Task RevokeUser_WithEventSystemUser_WithPushSyncOrgKeysOnRevokeRestoreEnabled_Success(Organization organization, [OrganizationUser] OrganizationUser organizationUser, EventSystemUser eventSystemUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, null, organizationUser, sutProvider);

        sutProvider.GetDependency<IFeatureService>()
            .IsEnabled(FeatureFlagKeys.PushSyncOrgKeysOnRevokeRestore)
            .Returns(true);

        await sutProvider.Sut.RevokeUserAsync(organizationUser, eventSystemUser);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RevokeAsync(organizationUser.Id);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Revoked, eventSystemUser);
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(1)
            .PushSyncOrgKeysAsync(organizationUser.UserId!.Value);
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_Success(Organization organization, [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        await sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RestoreAsync(organizationUser.Id, OrganizationUserStatusType.Invited);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Restored);
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_WithPushSyncOrgKeysOnRevokeRestoreEnabled_Success(Organization organization, [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IFeatureService>()
            .IsEnabled(FeatureFlagKeys.PushSyncOrgKeysOnRevokeRestore)
            .Returns(true);

        await sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RestoreAsync(organizationUser.Id, OrganizationUserStatusType.Invited);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Restored);
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(1)
            .PushSyncOrgKeysAsync(organizationUser.UserId!.Value);
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_WithEventSystemUser_Success(Organization organization, [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser, EventSystemUser eventSystemUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, null, organizationUser, sutProvider);

        await sutProvider.Sut.RestoreUserAsync(organizationUser, eventSystemUser);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RestoreAsync(organizationUser.Id, OrganizationUserStatusType.Invited);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Restored, eventSystemUser);
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_WithEventSystemUser_WithPushSyncOrgKeysOnRevokeRestoreEnabled_Success(Organization organization, [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser, EventSystemUser eventSystemUser, SutProvider<OrganizationService> sutProvider)
    {
        RestoreRevokeUser_Setup(organization, null, organizationUser, sutProvider);

        sutProvider.GetDependency<IFeatureService>()
            .IsEnabled(FeatureFlagKeys.PushSyncOrgKeysOnRevokeRestore)
            .Returns(true);

        await sutProvider.Sut.RestoreUserAsync(organizationUser, eventSystemUser);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RestoreAsync(organizationUser.Id, OrganizationUserStatusType.Invited);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Restored, eventSystemUser);
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(1)
            .PushSyncOrgKeysAsync(organizationUser.UserId!.Value);
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_RestoreThemselves_Fails(Organization organization, [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser, SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.UserId = owner.Id;
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("you cannot restore yourself", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory]
    [BitAutoData(OrganizationUserType.Admin)]
    [BitAutoData(OrganizationUserType.Custom)]
    public async Task RestoreUser_AdminRestoreOwner_Fails(OrganizationUserType restoringUserType,
        Organization organization, [OrganizationUser(OrganizationUserStatusType.Confirmed)] OrganizationUser restoringUser,
        [OrganizationUser(OrganizationUserStatusType.Revoked, OrganizationUserType.Owner)] OrganizationUser organizationUser, SutProvider<OrganizationService> sutProvider)
    {
        restoringUser.Type = restoringUserType;
        RestoreRevokeUser_Setup(organization, restoringUser, organizationUser, sutProvider);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, restoringUser.Id));

        Assert.Contains("only owners can restore other owners", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory]
    [BitAutoData(OrganizationUserStatusType.Invited)]
    [BitAutoData(OrganizationUserStatusType.Accepted)]
    [BitAutoData(OrganizationUserStatusType.Confirmed)]
    public async Task RestoreUser_WithStatusOtherThanRevoked_Fails(OrganizationUserStatusType userStatus, Organization organization, [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser] OrganizationUser organizationUser, SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Status = userStatus;
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("already active", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_WithOtherOrganizationSingleOrgPolicyEnabled_Fails(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null; // this is required to mock that the user as had already been confirmed before the revoke
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IPolicyService>()
            .AnyPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.SingleOrg, Arg.Any<OrganizationUserStatusType>())
            .Returns(true);

        var user = new User();
        user.Email = "test@bitwarden.com";
        sutProvider.GetDependency<IUserRepository>().GetByIdAsync(organizationUser.UserId.Value).Returns(user);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("test@bitwarden.com belongs to an organization that doesn't allow them to join multiple organizations", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_With2FAPolicyEnabled_WithoutUser2FAConfigured_Fails(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null;

        sutProvider.GetDependency<ITwoFactorIsEnabledQuery>()
            .TwoFactorIsEnabledAsync(Arg.Is<IEnumerable<Guid>>(i => i.Contains(organizationUser.UserId.Value)))
            .Returns(new List<(Guid userId, bool twoFactorIsEnabled)>() { (organizationUser.UserId.Value, false) });

        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IPolicyService>()
            .GetPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.TwoFactorAuthentication, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[] { new OrganizationUserPolicyDetails { OrganizationId = organizationUser.OrganizationId, PolicyType = PolicyType.TwoFactorAuthentication } });

        var user = new User();
        user.Email = "test@bitwarden.com";
        sutProvider.GetDependency<IUserRepository>().GetByIdAsync(organizationUser.UserId.Value).Returns(user);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("test@bitwarden.com is not compliant with the two-step login policy", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_With2FAPolicyEnabled_WithUser2FAConfigured_Success(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null; // this is required to mock that the user as had already been confirmed before the revoke
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IPolicyService>()
            .GetPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.TwoFactorAuthentication, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[] { new OrganizationUserPolicyDetails { OrganizationId = organizationUser.OrganizationId, PolicyType = PolicyType.TwoFactorAuthentication } });
        sutProvider.GetDependency<ITwoFactorIsEnabledQuery>()
            .TwoFactorIsEnabledAsync(Arg.Is<IEnumerable<Guid>>(i => i.Contains(organizationUser.UserId.Value)))
            .Returns(new List<(Guid userId, bool twoFactorIsEnabled)>() { (organizationUser.UserId.Value, true) });

        await sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RestoreAsync(organizationUser.Id, OrganizationUserStatusType.Confirmed);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Restored);
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_WithSingleOrgPolicyEnabled_Fails(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        [OrganizationUser(OrganizationUserStatusType.Accepted)] OrganizationUser secondOrganizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null; // this is required to mock that the user as had already been confirmed before the revoke
        secondOrganizationUser.UserId = organizationUser.UserId;
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IOrganizationUserRepository>()
            .GetManyByUserAsync(organizationUser.UserId.Value)
            .Returns(new[] { organizationUser, secondOrganizationUser });
        sutProvider.GetDependency<IPolicyService>()
            .GetPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.SingleOrg, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[]
            {
                new OrganizationUserPolicyDetails { OrganizationId = organizationUser.OrganizationId, PolicyType = PolicyType.SingleOrg, OrganizationUserStatus = OrganizationUserStatusType.Revoked }
            });

        var user = new User();
        user.Email = "test@bitwarden.com";
        sutProvider.GetDependency<IUserRepository>().GetByIdAsync(organizationUser.UserId.Value).Returns(user);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("test@bitwarden.com is not compliant with the single organization policy", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_vNext_WithOtherOrganizationSingleOrgPolicyEnabled_Fails(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        [OrganizationUser(OrganizationUserStatusType.Accepted)] OrganizationUser secondOrganizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null; // this is required to mock that the user as had already been confirmed before the revoke
        secondOrganizationUser.UserId = organizationUser.UserId;
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<ITwoFactorIsEnabledQuery>()
            .TwoFactorIsEnabledAsync(Arg.Is<IEnumerable<Guid>>(i => i.Contains(organizationUser.UserId.Value)))
            .Returns(new List<(Guid userId, bool twoFactorIsEnabled)>() { (organizationUser.UserId.Value, true) });

        sutProvider.GetDependency<IPolicyService>()
            .AnyPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.SingleOrg, Arg.Any<OrganizationUserStatusType>())
            .Returns(true);

        var user = new User();
        user.Email = "test@bitwarden.com";
        sutProvider.GetDependency<IUserRepository>().GetByIdAsync(organizationUser.UserId.Value).Returns(user);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("test@bitwarden.com belongs to an organization that doesn't allow them to join multiple organizations", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_WithSingleOrgPolicyEnabled_And_2FA_Policy_Fails(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        [OrganizationUser(OrganizationUserStatusType.Accepted)] OrganizationUser secondOrganizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null; // this is required to mock that the user as had already been confirmed before the revoke
        secondOrganizationUser.UserId = organizationUser.UserId;
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IOrganizationUserRepository>()
            .GetManyByUserAsync(organizationUser.UserId.Value)
            .Returns(new[] { organizationUser, secondOrganizationUser });
        sutProvider.GetDependency<IPolicyService>()
            .GetPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.SingleOrg, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[]
            {
                new OrganizationUserPolicyDetails { OrganizationId = organizationUser.OrganizationId, PolicyType = PolicyType.SingleOrg, OrganizationUserStatus = OrganizationUserStatusType.Revoked }
            });

        sutProvider.GetDependency<IPolicyService>()
            .GetPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.TwoFactorAuthentication, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[]
            {
                new OrganizationUserPolicyDetails { OrganizationId = organizationUser.OrganizationId, PolicyType = PolicyType.TwoFactorAuthentication, OrganizationUserStatus = OrganizationUserStatusType.Revoked }
            });

        var user = new User();
        user.Email = "test@bitwarden.com";
        sutProvider.GetDependency<IUserRepository>().GetByIdAsync(organizationUser.UserId.Value).Returns(user);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("test@bitwarden.com is not compliant with the single organization and two-step login polciy", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_vNext_With2FAPolicyEnabled_WithoutUser2FAConfigured_Fails(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null;

        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IPolicyService>()
            .GetPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.TwoFactorAuthentication, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[] { new OrganizationUserPolicyDetails { OrganizationId = organizationUser.OrganizationId, PolicyType = PolicyType.TwoFactorAuthentication } });

        var user = new User();
        user.Email = "test@bitwarden.com";
        sutProvider.GetDependency<IUserRepository>().GetByIdAsync(organizationUser.UserId.Value).Returns(user);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id));

        Assert.Contains("test@bitwarden.com is not compliant with the two-step login policy", exception.Message.ToLowerInvariant());

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .DidNotReceiveWithAnyArgs()
            .RestoreAsync(Arg.Any<Guid>(), Arg.Any<OrganizationUserStatusType>());
        await sutProvider.GetDependency<IEventService>()
            .DidNotReceiveWithAnyArgs()
            .LogOrganizationUserEventAsync(Arg.Any<OrganizationUser>(), Arg.Any<EventType>(), Arg.Any<EventSystemUser>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .DidNotReceiveWithAnyArgs()
            .PushSyncOrgKeysAsync(Arg.Any<Guid>());
    }

    [Theory, BitAutoData]
    public async Task RestoreUser_vNext_With2FAPolicyEnabled_WithUser2FAConfigured_Success(
        Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser organizationUser,
        SutProvider<OrganizationService> sutProvider)
    {
        organizationUser.Email = null; // this is required to mock that the user as had already been confirmed before the revoke
        RestoreRevokeUser_Setup(organization, owner, organizationUser, sutProvider);

        sutProvider.GetDependency<IPolicyService>()
            .GetPoliciesApplicableToUserAsync(organizationUser.UserId.Value, PolicyType.TwoFactorAuthentication, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[] { new OrganizationUserPolicyDetails { OrganizationId = organizationUser.OrganizationId, PolicyType = PolicyType.TwoFactorAuthentication } });

        sutProvider.GetDependency<ITwoFactorIsEnabledQuery>()
            .TwoFactorIsEnabledAsync(Arg.Is<IEnumerable<Guid>>(i => i.Contains(organizationUser.UserId.Value)))
            .Returns(new List<(Guid userId, bool twoFactorIsEnabled)>() { (organizationUser.UserId.Value, true) });

        await sutProvider.Sut.RestoreUserAsync(organizationUser, owner.Id);

        await sutProvider.GetDependency<IOrganizationUserRepository>()
            .Received(1)
            .RestoreAsync(organizationUser.Id, OrganizationUserStatusType.Confirmed);
        await sutProvider.GetDependency<IEventService>()
            .Received(1)
            .LogOrganizationUserEventAsync(organizationUser, EventType.OrganizationUser_Restored);
    }

    [Theory]
    [BitAutoData(PlanType.TeamsAnnually)]
    [BitAutoData(PlanType.TeamsMonthly)]
    [BitAutoData(PlanType.TeamsStarter)]
    [BitAutoData(PlanType.EnterpriseAnnually)]
    [BitAutoData(PlanType.EnterpriseMonthly)]
    public void ValidateSecretsManagerPlan_ThrowsException_WhenNoSecretsManagerSeats(PlanType planType, SutProvider<OrganizationService> sutProvider)
    {
        var plan = StaticStore.GetPlan(planType);
        var signup = new OrganizationUpgrade
        {
            UseSecretsManager = true,
            AdditionalSmSeats = 0,
            AdditionalServiceAccounts = 5,
            AdditionalSeats = 2
        };

        var exception = Assert.Throws<BadRequestException>(() => sutProvider.Sut.ValidateSecretsManagerPlan(plan, signup));
        Assert.Contains("You do not have any Secrets Manager seats!", exception.Message);
    }

    [Theory]
    [BitAutoData(PlanType.Free)]
    public void ValidateSecretsManagerPlan_ThrowsException_WhenSubtractingSeats(PlanType planType, SutProvider<OrganizationService> sutProvider)
    {
        var plan = StaticStore.GetPlan(planType);
        var signup = new OrganizationUpgrade
        {
            UseSecretsManager = true,
            AdditionalSmSeats = -1,
            AdditionalServiceAccounts = 5
        };
        var exception = Assert.Throws<BadRequestException>(() => sutProvider.Sut.ValidateSecretsManagerPlan(plan, signup));
        Assert.Contains("You can't subtract Secrets Manager seats!", exception.Message);
    }

    [Theory]
    [BitAutoData(PlanType.Free)]
    public void ValidateSecretsManagerPlan_ThrowsException_WhenPlanDoesNotAllowAdditionalServiceAccounts(
        PlanType planType,
        SutProvider<OrganizationService> sutProvider)
    {
        var plan = StaticStore.GetPlan(planType);
        var signup = new OrganizationUpgrade
        {
            UseSecretsManager = true,
            AdditionalSmSeats = 2,
            AdditionalServiceAccounts = 5,
            AdditionalSeats = 3
        };
        var exception = Assert.Throws<BadRequestException>(() => sutProvider.Sut.ValidateSecretsManagerPlan(plan, signup));
        Assert.Contains("Plan does not allow additional Machine Accounts.", exception.Message);
    }

    [Theory]
    [BitAutoData(PlanType.TeamsAnnually)]
    [BitAutoData(PlanType.TeamsMonthly)]
    [BitAutoData(PlanType.EnterpriseAnnually)]
    [BitAutoData(PlanType.EnterpriseMonthly)]
    public void ValidateSecretsManagerPlan_ThrowsException_WhenMoreSeatsThanPasswordManagerSeats(PlanType planType, SutProvider<OrganizationService> sutProvider)
    {
        var plan = StaticStore.GetPlan(planType);
        var signup = new OrganizationUpgrade
        {
            UseSecretsManager = true,
            AdditionalSmSeats = 4,
            AdditionalServiceAccounts = 5,
            AdditionalSeats = 3
        };
        var exception = Assert.Throws<BadRequestException>(() => sutProvider.Sut.ValidateSecretsManagerPlan(plan, signup));
        Assert.Contains("You cannot have more Secrets Manager seats than Password Manager seats.", exception.Message);
    }

    [Theory]
    [BitAutoData(PlanType.TeamsAnnually)]
    [BitAutoData(PlanType.TeamsMonthly)]
    [BitAutoData(PlanType.TeamsStarter)]
    [BitAutoData(PlanType.EnterpriseAnnually)]
    [BitAutoData(PlanType.EnterpriseMonthly)]
    public void ValidateSecretsManagerPlan_ThrowsException_WhenSubtractingServiceAccounts(
        PlanType planType,
        SutProvider<OrganizationService> sutProvider)
    {
        var plan = StaticStore.GetPlan(planType);
        var signup = new OrganizationUpgrade
        {
            UseSecretsManager = true,
            AdditionalSmSeats = 4,
            AdditionalServiceAccounts = -5,
            AdditionalSeats = 5
        };
        var exception = Assert.Throws<BadRequestException>(() => sutProvider.Sut.ValidateSecretsManagerPlan(plan, signup));
        Assert.Contains("You can't subtract Machine Accounts!", exception.Message);
    }

    [Theory]
    [BitAutoData(PlanType.Free)]
    public void ValidateSecretsManagerPlan_ThrowsException_WhenPlanDoesNotAllowAdditionalUsers(
        PlanType planType,
        SutProvider<OrganizationService> sutProvider)
    {
        var plan = StaticStore.GetPlan(planType);
        var signup = new OrganizationUpgrade
        {
            UseSecretsManager = true,
            AdditionalSmSeats = 2,
            AdditionalServiceAccounts = 0,
            AdditionalSeats = 5
        };
        var exception = Assert.Throws<BadRequestException>(() => sutProvider.Sut.ValidateSecretsManagerPlan(plan, signup));
        Assert.Contains("Plan does not allow additional users.", exception.Message);
    }

    [Theory]
    [BitAutoData(PlanType.TeamsAnnually)]
    [BitAutoData(PlanType.TeamsMonthly)]
    [BitAutoData(PlanType.TeamsStarter)]
    [BitAutoData(PlanType.EnterpriseAnnually)]
    [BitAutoData(PlanType.EnterpriseMonthly)]
    public void ValidateSecretsManagerPlan_ValidPlan_NoExceptionThrown(
        PlanType planType,
        SutProvider<OrganizationService> sutProvider)
    {
        var plan = StaticStore.GetPlan(planType);
        var signup = new OrganizationUpgrade
        {
            UseSecretsManager = true,
            AdditionalSmSeats = 2,
            AdditionalServiceAccounts = 0,
            AdditionalSeats = 4
        };

        sutProvider.Sut.ValidateSecretsManagerPlan(plan, signup);
    }

    [Theory]
    [OrganizationInviteCustomize(
         InviteeUserType = OrganizationUserType.Custom,
         InvitorUserType = OrganizationUserType.Custom
     ), BitAutoData]
    public async Task ValidateOrganizationUserUpdatePermissions_WithCustomPermission_WhenSavingUserHasCustomPermission_Passes(
        CurrentContextOrganization organization,
        OrganizationUserInvite organizationUserInvite,
        SutProvider<OrganizationService> sutProvider)
    {
        var invitePermissions = new Permissions { AccessReports = true };
        sutProvider.GetDependency<ICurrentContext>().GetOrganization(organization.Id).Returns(organization);
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organization.Id).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().AccessReports(organization.Id).Returns(true);

        await sutProvider.Sut.ValidateOrganizationUserUpdatePermissions(organization.Id, organizationUserInvite.Type.Value, null, invitePermissions);
    }

    [Theory]
    [OrganizationInviteCustomize(
         InviteeUserType = OrganizationUserType.Owner,
         InvitorUserType = OrganizationUserType.Admin
     ), BitAutoData]
    public async Task ValidateOrganizationUserUpdatePermissions_WithAdminAddingOwner_Throws(
        Guid organizationId,
        OrganizationUserInvite organizationUserInvite,
        SutProvider<OrganizationService> sutProvider)
    {
        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.ValidateOrganizationUserUpdatePermissions(organizationId, organizationUserInvite.Type.Value, null, organizationUserInvite.Permissions));

        Assert.Contains("only an owner can configure another owner's account.", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
        InviteeUserType = OrganizationUserType.Admin,
        InvitorUserType = OrganizationUserType.Owner
    ), BitAutoData]
    public async Task ValidateOrganizationUserUpdatePermissions_WithoutManageUsersPermission_Throws(
        Guid organizationId,
        OrganizationUserInvite organizationUserInvite,
        SutProvider<OrganizationService> sutProvider)
    {
        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.ValidateOrganizationUserUpdatePermissions(organizationId, organizationUserInvite.Type.Value, null, organizationUserInvite.Permissions));

        Assert.Contains("your account does not have permission to manage users.", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
         InviteeUserType = OrganizationUserType.Admin,
         InvitorUserType = OrganizationUserType.Custom
     ), BitAutoData]
    public async Task ValidateOrganizationUserUpdatePermissions_WithCustomAddingAdmin_Throws(
        Guid organizationId,
        OrganizationUserInvite organizationUserInvite,
        SutProvider<OrganizationService> sutProvider)
    {
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organizationId).Returns(true);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.ValidateOrganizationUserUpdatePermissions(organizationId, organizationUserInvite.Type.Value, null, organizationUserInvite.Permissions));

        Assert.Contains("custom users can not manage admins or owners.", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [OrganizationInviteCustomize(
         InviteeUserType = OrganizationUserType.Custom,
         InvitorUserType = OrganizationUserType.Custom
     ), BitAutoData]
    public async Task ValidateOrganizationUserUpdatePermissions_WithCustomAddingUser_WithoutPermissions_Throws(
        Guid organizationId,
        OrganizationUserInvite organizationUserInvite,
        SutProvider<OrganizationService> sutProvider)
    {
        var invitePermissions = new Permissions { AccessReports = true };
        sutProvider.GetDependency<ICurrentContext>().ManageUsers(organizationId).Returns(true);
        sutProvider.GetDependency<ICurrentContext>().AccessReports(organizationId).Returns(false);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.ValidateOrganizationUserUpdatePermissions(organizationId, organizationUserInvite.Type.Value, null, invitePermissions));

        Assert.Contains("custom users can only grant the same custom permissions that they have.", exception.Message.ToLowerInvariant());
    }

    [Theory]
    [BitAutoData(OrganizationUserType.Owner)]
    [BitAutoData(OrganizationUserType.Admin)]
    [BitAutoData(OrganizationUserType.User)]
    public async Task ValidateOrganizationCustomPermissionsEnabledAsync_WithNotCustomType_IsValid(
        OrganizationUserType newType,
        Guid organizationId,
        SutProvider<OrganizationService> sutProvider)
    {
        await sutProvider.Sut.ValidateOrganizationCustomPermissionsEnabledAsync(organizationId, newType);
    }

    [Theory, BitAutoData]
    public async Task ValidateOrganizationCustomPermissionsEnabledAsync_NotExistingOrg_ThrowsNotFound(
        Guid organizationId,
        SutProvider<OrganizationService> sutProvider)
    {
        await Assert.ThrowsAsync<NotFoundException>(() => sutProvider.Sut.ValidateOrganizationCustomPermissionsEnabledAsync(organizationId, OrganizationUserType.Custom));
    }

    [Theory, BitAutoData]
    public async Task ValidateOrganizationCustomPermissionsEnabledAsync_WithUseCustomPermissionsDisabled_ThrowsBadRequest(
        Organization organization,
        SutProvider<OrganizationService> sutProvider)
    {
        organization.UseCustomPermissions = false;

        sutProvider.GetDependency<IOrganizationRepository>()
            .GetByIdAsync(organization.Id)
            .Returns(organization);

        var exception = await Assert.ThrowsAsync<BadRequestException>(
            () => sutProvider.Sut.ValidateOrganizationCustomPermissionsEnabledAsync(organization.Id, OrganizationUserType.Custom));

        Assert.Contains("to enable custom permissions", exception.Message.ToLowerInvariant());
    }

    [Theory, BitAutoData]
    public async Task ValidateOrganizationCustomPermissionsEnabledAsync_WithUseCustomPermissionsEnabled_IsValid(
        Organization organization,
        SutProvider<OrganizationService> sutProvider)
    {
        organization.UseCustomPermissions = true;

        sutProvider.GetDependency<IOrganizationRepository>()
            .GetByIdAsync(organization.Id)
            .Returns(organization);

        await sutProvider.Sut.ValidateOrganizationCustomPermissionsEnabledAsync(organization.Id, OrganizationUserType.Custom);
    }

    // Must set real guids in order for dictionary of guids to not throw aggregate exceptions
    private void SetupOrgUserRepositoryCreateManyAsyncMock(IOrganizationUserRepository organizationUserRepository)
    {
        organizationUserRepository.CreateManyAsync(Arg.Any<IEnumerable<OrganizationUser>>()).Returns(
            info =>
            {
                var orgUsers = info.Arg<IEnumerable<OrganizationUser>>();
                foreach (var orgUser in orgUsers)
                {
                    orgUser.Id = Guid.NewGuid();
                }

                return Task.FromResult<ICollection<Guid>>(orgUsers.Select(u => u.Id).ToList());
            }
        );

        organizationUserRepository.CreateAsync(Arg.Any<OrganizationUser>(), Arg.Any<IEnumerable<CollectionAccessSelection>>()).Returns(
            info =>
            {
                var orgUser = info.Arg<OrganizationUser>();
                orgUser.Id = Guid.NewGuid();
                return Task.FromResult<Guid>(orgUser.Id);
            }
        );
    }

    // Must set real guids in order for dictionary of guids to not throw aggregate exceptions
    private void SetupOrgUserRepositoryCreateAsyncMock(IOrganizationUserRepository organizationUserRepository)
    {
        organizationUserRepository.CreateAsync(Arg.Any<OrganizationUser>(),
            Arg.Any<IEnumerable<CollectionAccessSelection>>()).Returns(
            info =>
            {
                var orgUser = info.Arg<OrganizationUser>();
                orgUser.Id = Guid.NewGuid();
                return Task.FromResult<Guid>(orgUser.Id);
            }
        );
    }

    [Theory, BitAutoData]
    public async Task RestoreUsers_Success(Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser orgUser1,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser orgUser2,
        SutProvider<OrganizationService> sutProvider)
    {
        // Arrange
        RestoreRevokeUser_Setup(organization, owner, orgUser1, sutProvider);
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        var eventService = sutProvider.GetDependency<IEventService>();
        var twoFactorIsEnabledQuery = sutProvider.GetDependency<ITwoFactorIsEnabledQuery>();
        var userService = Substitute.For<IUserService>();

        orgUser1.Email = orgUser2.Email = null; // Mock that users were previously confirmed
        orgUser1.OrganizationId = orgUser2.OrganizationId = organization.Id;
        organizationUserRepository
            .GetManyAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(orgUser1.Id) && ids.Contains(orgUser2.Id)))
            .Returns(new[] { orgUser1, orgUser2 });

        twoFactorIsEnabledQuery
            .TwoFactorIsEnabledAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(orgUser1.UserId!.Value) && ids.Contains(orgUser2.UserId!.Value)))
            .Returns(new List<(Guid userId, bool twoFactorIsEnabled)>
            {
                (orgUser1.UserId!.Value, true),
                (orgUser2.UserId!.Value, false)
            });

        // Act
        var result = await sutProvider.Sut.RestoreUsersAsync(organization.Id, new[] { orgUser1.Id, orgUser2.Id }, owner.Id, userService);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Empty(r.Item2)); // No error messages
        await organizationUserRepository
            .Received(1)
            .RestoreAsync(orgUser1.Id, OrganizationUserStatusType.Confirmed);
        await organizationUserRepository
            .Received(1)
            .RestoreAsync(orgUser2.Id, OrganizationUserStatusType.Confirmed);
        await eventService.Received(1)
            .LogOrganizationUserEventAsync(orgUser1, EventType.OrganizationUser_Restored);
        await eventService.Received(1)
            .LogOrganizationUserEventAsync(orgUser2, EventType.OrganizationUser_Restored);
    }

    [Theory, BitAutoData]
    public async Task RestoreUsers_With2FAPolicy_BlocksNonCompliantUser(Organization organization,
        [OrganizationUser(OrganizationUserStatusType.Confirmed, OrganizationUserType.Owner)] OrganizationUser owner,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser orgUser1,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser orgUser2,
        [OrganizationUser(OrganizationUserStatusType.Revoked)] OrganizationUser orgUser3,
        SutProvider<OrganizationService> sutProvider)
    {
        // Arrange
        RestoreRevokeUser_Setup(organization, owner, orgUser1, sutProvider);
        var organizationUserRepository = sutProvider.GetDependency<IOrganizationUserRepository>();
        var userRepository = sutProvider.GetDependency<IUserRepository>();
        var policyService = sutProvider.GetDependency<IPolicyService>();
        var userService = Substitute.For<IUserService>();

        orgUser1.Email = orgUser2.Email = null;
        orgUser3.UserId = null;
        orgUser3.Key = null;
        orgUser1.OrganizationId = orgUser2.OrganizationId = orgUser3.OrganizationId = organization.Id;
        organizationUserRepository
            .GetManyAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(orgUser1.Id) && ids.Contains(orgUser2.Id) && ids.Contains(orgUser3.Id)))
            .Returns(new[] { orgUser1, orgUser2, orgUser3 });

        userRepository.GetByIdAsync(orgUser2.UserId!.Value).Returns(new User { Email = "test@example.com" });

        // Setup 2FA policy
        policyService.GetPoliciesApplicableToUserAsync(Arg.Any<Guid>(), PolicyType.TwoFactorAuthentication, Arg.Any<OrganizationUserStatusType>())
            .Returns(new[] { new OrganizationUserPolicyDetails { OrganizationId = organization.Id, PolicyType = PolicyType.TwoFactorAuthentication } });

        // User1 has 2FA, User2 doesn't
        sutProvider.GetDependency<ITwoFactorIsEnabledQuery>()
            .TwoFactorIsEnabledAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(orgUser1.UserId!.Value) && ids.Contains(orgUser2.UserId!.Value)))
            .Returns(new List<(Guid userId, bool twoFactorIsEnabled)>
            {
                (orgUser1.UserId!.Value, true),
                (orgUser2.UserId!.Value, false)
            });

        // Act
        var result = await sutProvider.Sut.RestoreUsersAsync(organization.Id, new[] { orgUser1.Id, orgUser2.Id, orgUser3.Id }, owner.Id, userService);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Empty(result[0].Item2); // First user should succeed
        Assert.Contains("two-step login", result[1].Item2); // Second user should fail
        Assert.Empty(result[2].Item2); // Third user should succeed
        await organizationUserRepository
            .Received(1)
            .RestoreAsync(orgUser1.Id, OrganizationUserStatusType.Confirmed);
        await organizationUserRepository
            .DidNotReceive()
            .RestoreAsync(orgUser2.Id, Arg.Any<OrganizationUserStatusType>());
        await organizationUserRepository
            .Received(1)
            .RestoreAsync(orgUser3.Id, OrganizationUserStatusType.Invited);
    }
}
