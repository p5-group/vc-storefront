using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using FluentValidation.AspNetCore;
using GraphQL.Client.Abstractions;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using ProxyKit;
using Swashbuckle.AspNetCore.SwaggerGen;
using VirtoCommerce.LiquidThemeEngine;
using VirtoCommerce.Storefront.Caching;
using VirtoCommerce.Storefront.DependencyInjection;
using VirtoCommerce.Storefront.Domain;
using VirtoCommerce.Storefront.Domain.Security;
using VirtoCommerce.Storefront.Extensions;
using VirtoCommerce.Storefront.Filters;
using VirtoCommerce.Storefront.Infrastructure;
using VirtoCommerce.Storefront.Infrastructure.ApplicationInsights;
using VirtoCommerce.Storefront.Infrastructure.Autorest;
using VirtoCommerce.Storefront.Infrastructure.HealthCheck;
using VirtoCommerce.Storefront.Infrastructure.Swagger;
using VirtoCommerce.Storefront.Middleware;
using VirtoCommerce.Storefront.Model;
using VirtoCommerce.Storefront.Model.Common;
using VirtoCommerce.Storefront.Model.Common.Bus;
using VirtoCommerce.Storefront.Model.Common.Events;
using VirtoCommerce.Storefront.Model.Customer.Services;
using VirtoCommerce.Storefront.Model.Features;
using VirtoCommerce.Storefront.Model.LinkList.Services;
using VirtoCommerce.Storefront.Model.Security;
using VirtoCommerce.Storefront.Model.StaticContent;
using VirtoCommerce.Storefront.Model.Stores;
using VirtoCommerce.Storefront.Routing;
using VirtoCommerce.Tools;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;

namespace VirtoCommerce.Storefront
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment hostingEnviroment)
        {
            Configuration = configuration;
            HostingEnvironment = hostingEnviroment;
        }

        public IConfiguration Configuration { get; }
        public IWebHostEnvironment HostingEnvironment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMemoryCache();
            services.AddResponseCaching();

            services.AddHealthChecks()
                .AddCheck<PlatformConnectionHealthChecker>("Platform connection health",
                    failureStatus: HealthStatus.Unhealthy,
                    tags: new[] { "PlatformConnection" });

            services.Configure<StorefrontOptions>(Configuration.GetSection("VirtoCommerce"));

            //The IHttpContextAccessor service is not registered by default
            //https://github.com/aspnet/Hosting/issues/793
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IWorkContextAccessor, WorkContextAccessor>();
            services.AddSingleton<IUrlBuilder, UrlBuilder>();
            services.AddSingleton<IStorefrontUrlBuilder, StorefrontUrlBuilder>();

            services.AddSingleton<IStoreService, StoreService>();
            services.AddSingleton<ICurrencyService, CurrencyService>();
            services.AddSingleton<ISlugRouteService, SlugRouteService>();
            services.AddSingleton<IMemberService, MemberService>();
            services.AddSingleton<ISeoInfoService, SeoInfoService>();
            services.AddSingleton<ISpaRouteService, SpaRouteService>();

            services.AddSingleton<IStaticContentService, StaticContentService>();
            services.AddSingleton<IMenuLinkListService, MenuLinkListServiceImpl>();
            services.AddSingleton<IStaticContentItemFactory, StaticContentItemFactory>();
            services.AddSingleton<IStaticContentLoaderFactory, StaticContentLoaderFactory>();
            services.AddSingleton<IApiChangesWatcher, ApiChangesWatcher>();
            services.AddSingleton<IBlobChangesWatcher, BlobChangesWatcher>();
            services.AddTransient<AngularAntiforgeryCookieResultFilterAttribute>();
            services.AddTransient<AnonymousUserForStoreAuthorizationFilter>();

            //Register events framework dependencies
            services.AddSingleton(new InProcessBus());
            services.AddSingleton<IEventPublisher>(provider => provider.GetService<InProcessBus>());
            services.AddSingleton<IHandlerRegistrar>(provider => provider.GetService<InProcessBus>());

            // register features toggling agent
            services.AddSingleton<IFeaturesAgent, FeaturesAgent>();

            //Cache
            var redisConnectionString = Configuration.GetConnectionString("RedisConnectionString");
            services.AddStorefrontCache(redisConnectionString, o =>
            {
                Configuration.GetSection("VirtoCommerce:Redis").Bind(o);
            });


            //Register platform API clients
            services.AddPlatformEndpoint(options =>
            {
                Configuration.GetSection("VirtoCommerce:Endpoint").Bind(options);
            });


            services.AddSingleton<ICountriesService, FileSystemCountriesService>();
            services.Configure<FileSystemCountriesOptions>(options =>
            {
                options.FilePath = HostingEnvironment.MapPath("~/countries.json");
            });

            var contentConnectionString = BlobConnectionString.Parse(Configuration.GetConnectionString("ContentConnectionString"));
            if (contentConnectionString.Provider.EqualsInvariant("AzureBlobStorage"))
            {
                var azureBlobOptions = new AzureBlobContentOptions();
                Configuration.GetSection("VirtoCommerce:AzureBlobStorage").Bind(azureBlobOptions);

                services.AddAzureBlobContent(options =>
                {
                    options.Container = contentConnectionString.RootPath;
                    options.ConnectionString = contentConnectionString.ConnectionString;
                    options.PollForChanges = azureBlobOptions.PollForChanges;
                    options.ChangesPollingInterval = azureBlobOptions.ChangesPollingInterval;
                });
            }
            else
            {
                var fileSystemBlobOptions = new FileSystemBlobContentOptions();
                Configuration.GetSection("VirtoCommerce:FileSystemBlobStorage").Bind(fileSystemBlobOptions);
                services.AddFileSystemBlobContent(options =>
                {
                    options.Path = HostingEnvironment.MapPath(contentConnectionString.RootPath);
                });
            }

            //Identity overrides for use remote user storage
            services.AddScoped<IUserStore<User>, UserStoreStub>();
            services.AddScoped<IRoleStore<Role>, UserStoreStub>();
            services.AddScoped<UserManager<User>, CustomUserManager>();
            services.AddScoped<SignInManager<User>, CustomSignInManager>();

            //Resource-based authorization that requires API permissions for some operations
            services.AddSingleton<IAuthorizationHandler, CanImpersonateAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, CanReadContentItemAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, OnlyRegisteredUserAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, AnonymousUserForStoreAuthorizationHandler>();
            // register the AuthorizationPolicyProvider which dynamically registers authorization policies for each permission defined in the platform 
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            //Storefront authorization handler for policy based on permissions 
            services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, CanEditOrganizationResourceAuthorizationHandler>();

            services.AddAuthorization(options =>
            {
                options.AddPolicy(CanImpersonateAuthorizationRequirement.PolicyName,
                                policy => policy.Requirements.Add(new CanImpersonateAuthorizationRequirement()));
                options.AddPolicy(CanReadContentItemAuthorizeRequirement.PolicyName,
                                policy => policy.Requirements.Add(new CanReadContentItemAuthorizeRequirement()));
                options.AddPolicy(CanEditOrganizationResourceAuthorizeRequirement.PolicyName,
                                policy => policy.Requirements.Add(new CanEditOrganizationResourceAuthorizeRequirement()));
                options.AddPolicy(OnlyRegisteredUserAuthorizationRequirement.PolicyName,
                                policy => policy.Requirements.Add(new OnlyRegisteredUserAuthorizationRequirement()));
                options.AddPolicy(AnonymousUserForStoreAuthorizationRequirement.PolicyName,
                                policy => policy.Requirements.Add(new AnonymousUserForStoreAuthorizationRequirement()));
            });

            var auth = services.AddAuthentication();

            var facebookSection = Configuration.GetSection("Authentication:Facebook");
            if (facebookSection.GetChildren().Any())
            {
                auth.AddFacebook(facebookOptions =>
                {
                    facebookSection.Bind(facebookOptions);
                });
            }
            var googleSection = Configuration.GetSection("Authentication:Google");
            if (googleSection.GetChildren().Any())
            {
                auth.AddGoogle(googleOptions =>
                {
                    googleSection.Bind(googleOptions);
                });
            }
            var githubSection = Configuration.GetSection("Authentication:Github");
            if (githubSection.GetChildren().Any())
            {
                auth.AddGitHub(GitHubAuthenticationOptions =>
                {
                    githubSection.Bind(GitHubAuthenticationOptions);
                });
            }
            var stackexchangeSection = Configuration.GetSection("Authentication:Stackexchange");

            if (stackexchangeSection.GetChildren().Any())
            {
                auth.AddStackExchange(StackExchangeAuthenticationOptions =>
                {
                    stackexchangeSection.Bind(StackExchangeAuthenticationOptions);
                });
            }

            services.Configure<IdentityOptions>(Configuration.GetSection("IdentityOptions"));
            services.AddIdentity<User, Role>(options => { }).AddDefaultTokenProviders();

            services.AddScoped<CustomCookieAuthenticationEvents>();
            services.ConfigureApplicationCookie(options =>
            {
                Configuration.GetSection("CookieAuthenticationOptions").Bind(options);
                options.EventsType = typeof(CustomCookieAuthenticationEvents);
            });

            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.Lax;
            });
            // The Tempdata provider cookie is not essential. Make it essential
            // so Tempdata is functional when tracking is disabled.
            services.Configure<CookieTempDataProviderOptions>(options =>
            {
                options.Cookie.IsEssential = true;
            });
            // Add Liquid view engine
            services.AddLiquidViewEngine(options =>
            {
                Configuration.GetSection("VirtoCommerce:LiquidThemeEngine").Bind(options);
            });

            services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-XSRF-TOKEN";
                options.SuppressXFrameOptionsHeader = true;
            });
            services.AddMvc(options =>
            {
                // Thus we disable anonymous users based on "Store:AllowAnonymous" store option
                options.Filters.AddService<AnonymousUserForStoreAuthorizationFilter>();

                options.CacheProfiles.Add("Default", new CacheProfile()
                {
                    Duration = (int)TimeSpan.FromHours(1).TotalSeconds,
                    VaryByHeader = "host"
                });
                options.CacheProfiles.Add("None", new CacheProfile()
                {
                    NoStore = true,
                    Location = ResponseCacheLocation.None
                });

                options.Filters.AddService(typeof(AngularAntiforgeryCookieResultFilterAttribute));

                // To include only Api controllers to swagger document
                options.Conventions.Add(new ApiExplorerApiControllersConvention());

                // Use the routing logic of ASP.NET Core 2.1 or earlier:
                options.EnableEndpointRouting = false;
            }).AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ContractResolver = new DefaultContractResolver()
                {
                    NamingStrategy = new CamelCaseNamingStrategy()
                };
                options.SerializerSettings.DefaultValueHandling = DefaultValueHandling.Include;
                options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
                options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                options.SerializerSettings.MissingMemberHandling = MissingMemberHandling.Ignore;
            })
             .AddFluentValidation();


            // Register event handlers via reflection
            services.RegisterAssembliesEventHandlers(typeof(Startup));

            // The following line enables Application Insights telemetry collection.
            services.AddApplicationInsightsTelemetry();
            services.AddApplicationInsightsExtensions(Configuration);


            // https://github.com/aspnet/HttpAbstractions/issues/315
            // Changing the default html encoding options, to not encode non-Latin characters
            services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

            services.Configure<HstsOptions>(options =>
            {
                options.IncludeSubDomains = true;
                options.MaxAge = TimeSpan.FromDays(30);
            });

            // Register the Swagger generator, defining 1 or more Swagger documents
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Storefront REST API documentation", Version = "v1" });
                c.IgnoreObsoleteProperties();
                c.IgnoreObsoleteActions();
                // To include 401 response type to actions that requires Authorization
                c.OperationFilter<AuthResponsesOperationFilter>();
                c.OperationFilter<ConsumeFromBodyFilter>();
                c.OperationFilter<FileResponseTypeFilter>();
                c.OperationFilter<OptionalParametersFilter>();
                c.OperationFilter<ArrayInQueryParametersFilter>();
                c.OperationFilter<FileUploadOperationFilter>();
                c.SchemaFilter<EnumSchemaFilter>();
                c.SchemaFilter<NewtonsoftJsonIgnoreFilter>();

                // Use method name as operation ID, i.e. ApiAccount.GetOrganization instead of /storefrontapi/account/organization (will be treated as just organization method)
                c.CustomOperationIds(apiDesc => apiDesc.TryGetMethodInfo(out var methodInfo) ? methodInfo.Name : null);

                // To avoid errors with repeating type names
                c.CustomSchemaIds(type => (Attribute.GetCustomAttribute(type, typeof(SwaggerSchemaIdAttribute)) as SwaggerSchemaIdAttribute)?.Id ?? type.FriendlyId());
            });

            services.AddResponseCompression();

            services.AddProxy(builder => builder.AddHttpMessageHandler(sp => sp.GetService<AuthenticationHandlerFactory>().CreateAuthHandler()));

            services.AddSingleton<IGraphQLClient>(s =>
            {
                var platformEndpointOptions = s.GetRequiredService<IOptions<PlatformEndpointOptions>>().Value;
                return new GraphQLHttpClient($"{platformEndpointOptions.Url}graphql", new NewtonsoftJsonSerializer());
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseForwardedHeaders();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/error/500");
                app.UseHsts();
            }
            // Do not write telemetry to debug output 
            TelemetryDebugWriter.IsTracingDisabled = true;

            app.UseHealthChecks("/storefrontapi/health", new HealthCheckOptions
            {
                ResponseWriter = async (context, report) =>
                {
                    context.Response.ContentType = "application/json; charset=utf-8";

                    var reportJson =
                        JsonConvert.SerializeObject(report.Entries, Formatting.Indented, new StringEnumConverter());
                    await context.Response.WriteAsync(reportJson);
                }
            });

            app.UseResponseCaching();

            app.UseResponseCompression();

            app.UseStaticFiles();
            app.UseCookiePolicy();
            app.UseRouting();

            app.UseAuthentication();


            // WorkContextBuildMiddleware must  always be registered first in  the Middleware chain
            app.UseMiddleware<WorkContextBuildMiddleware>();
            app.UseMiddleware<StoreMaintenanceMiddleware>();
            app.UseMiddleware<NoLiquidThemeMiddleware>();

            var mvcViewOptions = app.ApplicationServices.GetService<IOptions<MvcViewOptions>>().Value;
            mvcViewOptions.ViewEngines.Add(app.ApplicationServices.GetService<ILiquidViewEngine>());

            // Do not use status code pages for Api requests
            app.UseWhen(context => !context.Request.Path.IsApi(), appBuilder =>
            {
                appBuilder.UseStatusCodePagesWithReExecute("/error/{0}");
            });

            // Enable middleware to serve generated Swagger as a JSON endpoint.
            app.UseSwagger(c => c.RouteTemplate = "docs/{documentName}/docs.json");

            var rewriteOptions = new RewriteOptions();
            // Load IIS url rewrite rules from external file
            if (File.Exists("IISUrlRewrite.xml"))
            {
                using var iisUrlRewriteStreamReader = File.OpenText("IISUrlRewrite.xml");
                rewriteOptions.AddIISUrlRewrite(iisUrlRewriteStreamReader);
            }

            var requireHttpsOptions = new RequireHttpsOptions();
            Configuration.GetSection("VirtoCommerce:RequireHttps").Bind(requireHttpsOptions);
            if (requireHttpsOptions.Enabled)
            {
                rewriteOptions.AddRedirectToHttps(requireHttpsOptions.StatusCode, requireHttpsOptions.Port);
            }
            app.UseRewriter(rewriteOptions);
            // Enable browser XSS protection
            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Xss-Protection"] = "1";
                await next();
            });

            var platformEndpointOptions = app.ApplicationServices.GetRequiredService<IOptions<PlatformEndpointOptions>>().Value;
            // Forwards the request only when the host is set to the specified value
            app.UseWhen(
                context => context.Request.Path.Value.EndsWith("xapi/graphql"),
                appInner => appInner.RunProxy(context =>
                {
                    context.Request.Path = PathString.Empty;
                    return context.ForwardTo(new Uri(platformEndpointOptions.Url, "graphql"))
                        .AddXForwardedHeaders()
                        .Send();
                }));

            // It will be good to rewrite endpoint routing as described here, but it's not easy to do:
            // https://docs.microsoft.com/en-us/aspnet/core/migration/22-to-30?view=aspnetcore-3.1&tabs=visual-studio#routing-startup-code
            app.UseMvc(routes =>
            {
                routes.MapSlugRoute("{*path}", defaults: new { controller = "Home", action = "Index" });
            });
        }
    }
}
