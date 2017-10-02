﻿using System;
using System.Threading.Tasks;
using AutoMapper;
using Exceptionless.Api.Hubs;
using Exceptionless.Api.Models;
using Exceptionless.Api.Security;
using Exceptionless.Api.Utility;
using Exceptionless.Api.Utility.Handlers;
using Exceptionless.Core;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Jobs.WorkItemHandlers;
using Exceptionless.Core.Models;
using Exceptionless.Core.Models.Data;
using Exceptionless.Core.Queues.Models;
using Exceptionless.Core.Utility;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Exceptionless.Api {
    public class Bootstrapper {
        public static void RegisterServices(IServiceCollection container, ILoggerFactory loggerFactory, bool includeInsulation = false) {
            var config = container.BuildServiceProvider().GetService<IConfiguration>();
            Settings.Initialize(config);

            container.AddSingleton<WebSocketConnectionManager>();
            container.AddSingleton<MessageBusBroker>();
            container.AddSingleton<MessageBusBrokerMiddleware>();
            container.AddSingleton<IConnectionMapping, ConnectionMapping>();

            container.AddSingleton<ApiKeyMiddleware>();
            container.AddSingleton<OverageMiddleware>();
            container.AddSingleton<ThrottlingMiddleware>();

            container.AddTransient<Profile, ApiMappings>();

            Core.Bootstrapper.RegisterServices(container, loggerFactory);
            if (includeInsulation)
                Insulation.Bootstrapper.RegisterServices(container, Settings.Current.RunJobsInProcess, loggerFactory);

            if (Settings.Current.RunJobsInProcess)
                container.AddSingleton<IHostedService, JobsHostedService>();

            var logger = loggerFactory.CreateLogger<Startup>();
            container.AddStartupAction<MessageBusBroker>();
            container.AddStartupAction((sp, ct) => {
                var subscriber = sp.GetRequiredService<IMessageSubscriber>();
                return subscriber.SubscribeAsync<WorkItemStatus>(workItemStatus => {
                    if (logger.IsEnabled(LogLevel.Trace))
                        logger.LogTrace("WorkItem id:{WorkItemId} message:{Message} progress:{Progress}", workItemStatus.WorkItemId ?? "<NULL>", workItemStatus.Message ?? "<NULL>", workItemStatus.Progress);

                    return Task.CompletedTask;
                }, ct);
            });

            container.AddSingleton<EnqueueOrganizationNotificationOnPlanOverage>();
            container.AddStartupAction<EnqueueOrganizationNotificationOnPlanOverage>();
        }

        public class ApiMappings : Profile {
            public ApiMappings() {
                CreateMap<UserDescription, EventUserDescription>();

                CreateMap<NewOrganization, Organization>();
                CreateMap<Organization, ViewOrganization>().AfterMap((o, vo) => {
                    vo.IsOverHourlyLimit = o.IsOverHourlyLimit();
                    vo.IsOverMonthlyLimit = o.IsOverMonthlyLimit();
                });

                CreateMap<StripeInvoice, InvoiceGridModel>().AfterMap((si, igm) => igm.Id = igm.Id.Substring(3));

                CreateMap<NewProject, Project>();
                CreateMap<Project, ViewProject>().AfterMap((p, vp) => vp.HasSlackIntegration = p.Data.ContainsKey(Project.KnownDataKeys.SlackToken));

                CreateMap<NewToken, Token>().ForMember(m => m.Type, m => m.Ignore());
                CreateMap<Token, ViewToken>();

                CreateMap<User, ViewUser>();

                CreateMap<NewWebHook, WebHook>();
            }
        }
    }
}