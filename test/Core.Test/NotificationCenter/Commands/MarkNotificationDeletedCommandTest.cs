﻿#nullable enable
using System.Security.Claims;
using Bit.Core.Context;
using Bit.Core.Exceptions;
using Bit.Core.NotificationCenter.Authorization;
using Bit.Core.NotificationCenter.Commands;
using Bit.Core.NotificationCenter.Entities;
using Bit.Core.NotificationCenter.Repositories;
using Bit.Core.Platform.Push;
using Bit.Core.Test.NotificationCenter.AutoFixture;
using Bit.Test.Common.AutoFixture;
using Bit.Test.Common.AutoFixture.Attributes;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Bit.Core.Test.NotificationCenter.Commands;

[SutProviderCustomize]
[NotificationCustomize]
[NotificationStatusCustomize]
public class MarkNotificationDeletedCommandTest
{
    private static void Setup(SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Guid? userId, Notification? notification, NotificationStatus? notificationStatus,
        bool authorizedNotification = false, bool authorizedCreate = false, bool authorizedUpdate = false)
    {
        sutProvider.GetDependency<ICurrentContext>().UserId.Returns(userId);
        sutProvider.GetDependency<INotificationRepository>()
            .GetByIdAsync(notificationId)
            .Returns(notification);
        sutProvider.GetDependency<INotificationStatusRepository>()
            .GetByNotificationIdAndUserIdAsync(notificationId, userId ?? Arg.Any<Guid>())
            .Returns(notificationStatus);
        sutProvider.GetDependency<INotificationStatusRepository>()
            .CreateAsync(Arg.Any<NotificationStatus>());
        sutProvider.GetDependency<INotificationStatusRepository>()
            .UpdateAsync(notificationStatus ?? Arg.Any<NotificationStatus>());
        sutProvider.GetDependency<IAuthorizationService>()
            .AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), notification ?? Arg.Any<Notification>(),
                Arg.Is<IEnumerable<IAuthorizationRequirement>>(reqs =>
                    reqs.Contains(NotificationOperations.Read)))
            .Returns(authorizedNotification ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        sutProvider.GetDependency<IAuthorizationService>()
            .AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), notificationStatus ?? Arg.Any<NotificationStatus>(),
                Arg.Is<IEnumerable<IAuthorizationRequirement>>(reqs =>
                    reqs.Contains(NotificationStatusOperations.Create)))
            .Returns(authorizedCreate ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        sutProvider.GetDependency<IAuthorizationService>()
            .AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), notificationStatus ?? Arg.Any<NotificationStatus>(),
                Arg.Is<IEnumerable<IAuthorizationRequirement>>(reqs =>
                    reqs.Contains(NotificationStatusOperations.Update)))
            .Returns(authorizedUpdate ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        sutProvider.GetDependency<INotificationStatusRepository>().ClearReceivedCalls();
    }

    [Theory]
    [BitAutoData]
    public async Task MarkDeletedAsync_NotLoggedIn_NotFoundException(
        SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Notification notification, NotificationStatus notificationStatus)
    {
        Setup(sutProvider, notificationId, userId: null, notification, notificationStatus, true, true, true);

        await Assert.ThrowsAsync<NotFoundException>(() => sutProvider.Sut.MarkDeletedAsync(notificationId));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationStatusAsync(Arg.Any<Notification>(), Arg.Any<NotificationStatus>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationAsync(Arg.Any<Notification>());
    }

    [Theory]
    [BitAutoData]
    public async Task MarkDeletedAsync_NotificationNotFound_NotFoundException(
        SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Guid userId, NotificationStatus notificationStatus)
    {
        Setup(sutProvider, notificationId, userId, notification: null, notificationStatus, true, true, true);

        await Assert.ThrowsAsync<NotFoundException>(() => sutProvider.Sut.MarkDeletedAsync(notificationId));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationStatusAsync(Arg.Any<Notification>(), Arg.Any<NotificationStatus>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationAsync(Arg.Any<Notification>());
    }

    [Theory]
    [BitAutoData]
    public async Task MarkDeletedAsync_ReadRequirementNotificationNotAuthorized_NotFoundException(
        SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Guid userId, Notification notification, NotificationStatus notificationStatus)
    {
        Setup(sutProvider, notificationId, userId, notification, notificationStatus, authorizedNotification: false,
            true, true);

        await Assert.ThrowsAsync<NotFoundException>(() => sutProvider.Sut.MarkDeletedAsync(notificationId));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationStatusAsync(Arg.Any<Notification>(), Arg.Any<NotificationStatus>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationAsync(Arg.Any<Notification>());
    }

    [Theory]
    [BitAutoData]
    public async Task MarkDeletedAsync_CreateRequirementNotAuthorized_NotFoundException(
        SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Guid userId, Notification notification)
    {
        Setup(sutProvider, notificationId, userId, notification, notificationStatus: null, true,
            authorizedCreate: false, true);

        await Assert.ThrowsAsync<NotFoundException>(() => sutProvider.Sut.MarkDeletedAsync(notificationId));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationStatusAsync(Arg.Any<Notification>(), Arg.Any<NotificationStatus>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationAsync(Arg.Any<Notification>());
    }

    [Theory]
    [BitAutoData]
    public async Task MarkDeletedAsync_UpdateRequirementNotAuthorized_NotFoundException(
        SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Guid userId, Notification notification, NotificationStatus notificationStatus)
    {
        Setup(sutProvider, notificationId, userId, notification, notificationStatus, true, true,
            authorizedUpdate: false);

        await Assert.ThrowsAsync<NotFoundException>(() => sutProvider.Sut.MarkDeletedAsync(notificationId));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationStatusAsync(Arg.Any<Notification>(), Arg.Any<NotificationStatus>());
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationAsync(Arg.Any<Notification>());
    }

    [Theory]
    [BitAutoData]
    public async Task MarkDeletedAsync_NotificationStatusNotFoundCreateAuthorized_NotificationStatusCreated(
        SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Guid userId, Notification notification)
    {
        Setup(sutProvider, notificationId, userId, notification, notificationStatus: null, true, true, true);
        var expectedNotificationStatus = new NotificationStatus
        {
            NotificationId = notificationId,
            UserId = userId,
            ReadDate = null,
            DeletedDate = DateTime.UtcNow
        };

        await sutProvider.Sut.MarkDeletedAsync(notificationId);

        await sutProvider.GetDependency<INotificationStatusRepository>().Received(1)
            .CreateAsync(Arg.Do<NotificationStatus>(ns => AssertNotificationStatus(expectedNotificationStatus, ns)));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(1)
            .PushNotificationStatusAsync(notification,
                Arg.Do<NotificationStatus>(ns => AssertNotificationStatus(expectedNotificationStatus, ns)));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationAsync(Arg.Any<Notification>());
    }

    [Theory]
    [BitAutoData]
    public async Task MarkDeletedAsync_NotificationStatusFoundCreateAuthorized_NotificationStatusUpdated(
        SutProvider<MarkNotificationDeletedCommand> sutProvider,
        Guid notificationId, Guid userId, Notification notification, NotificationStatus notificationStatus)
    {
        Setup(sutProvider, notificationId, userId, notification, notificationStatus, true, true, true);

        await sutProvider.Sut.MarkDeletedAsync(notificationId);

        await sutProvider.GetDependency<INotificationStatusRepository>().Received(1)
            .UpdateAsync(Arg.Do<NotificationStatus>(ns => AssertNotificationStatus(notificationStatus, ns)));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(1)
            .PushNotificationStatusAsync(notification,
                Arg.Do<NotificationStatus>(ns => AssertNotificationStatus(notificationStatus, ns)));
        await sutProvider.GetDependency<IPushNotificationService>()
            .Received(0)
            .PushNotificationAsync(Arg.Any<Notification>());
    }

    private static void AssertNotificationStatus(NotificationStatus expectedNotificationStatus,
        NotificationStatus? actualNotificationStatus)
    {
        Assert.NotNull(actualNotificationStatus);
        Assert.Equal(expectedNotificationStatus.NotificationId, actualNotificationStatus.NotificationId);
        Assert.Equal(expectedNotificationStatus.UserId, actualNotificationStatus.UserId);
        Assert.Equal(expectedNotificationStatus.ReadDate, actualNotificationStatus.ReadDate);
        Assert.NotEqual(expectedNotificationStatus.DeletedDate, actualNotificationStatus.DeletedDate);
        Assert.NotNull(actualNotificationStatus.DeletedDate);
        Assert.Equal(DateTime.UtcNow, actualNotificationStatus.DeletedDate.Value, TimeSpan.FromMinutes(1));
    }
}
