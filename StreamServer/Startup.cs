using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;

namespace StreamServer;

public class Startup : IStartup
{
    readonly string MyAllowSpecificOrigins = "_myAllowSpecificOrigins";

    public Startup(IConfiguration configuration, IWebHostEnvironment environment)
    {
        Configuration = configuration;
        Environment = environment;
    }

    public IConfiguration Configuration { get; }
    public IWebHostEnvironment Environment { get; private set; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(x => x.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
        //services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddMvc().AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix).AddDataAnnotationsLocalization();
        services.AddEndpointsApiExplorer();
        //services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Startup).Assembly));
        //services.AddAutoMapper(typeof(Startup).GetTypeInfo().Assembly);
        services.AddHttpContextAccessor();
        services.AddHealthChecks();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "Innoku Management API", Version = "v1" });
            //c.SchemaFilter<SwaggerSchemaFilter>();
            //c.OperationFilter<SwaggerOperationFilter>();

            c.AddSecurityDefinition("bearer", new OpenApiSecurityScheme()
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                Description = "Jwt authorization header using the Bearer scheme."
            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement() {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "bearer"
                        }
                    },
                    new string[] {}
                }
            });

            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var filePath = Path.Combine(System.AppContext.BaseDirectory, xmlFile);
            c.IncludeXmlComments(filePath);
        });

        //if (Environment.IsProduction())
        //{
        //    services.AddStackExchangeRedisCache(action => {
        //        action.InstanceName = Configuration.GetValue<string>("Redis:InstanceName");
        //        action.Configuration = Configuration.GetValue<string>("Redis:Connection");
        //    });
        //}
        //else
        //{
        services.AddDistributedMemoryCache();
        //}

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = true;
        });

        //services.Configure<RequestLocalizationOptions>(options =>
        //{
        //    var supportedCultures = new[]
        //    {
        //        new CultureInfo(LanguageEnum.Spanish.GetAttributeOfType<DescriptionAttribute>().Description),
        //        new CultureInfo(LanguageEnum.English.GetAttributeOfType<DescriptionAttribute>().Description)
        //    };
        //    options.DefaultRequestCulture = new RequestCulture(
        //        culture: LanguageEnum.Spanish.GetAttributeOfType<DescriptionAttribute>().Description,
        //        uiCulture: LanguageEnum.Spanish.GetAttributeOfType<DescriptionAttribute>().Description);
        //    options.SupportedCultures = supportedCultures;
        //    options.SupportedUICultures = supportedCultures;

        //    options.RequestCultureProviders.Clear();

        //    options.RequestCultureProviders.Insert(0,
        //        new AcceptLanguageHeaderRequestCultureProvider()
        //        {
        //            Options = options
        //        });
        //});

        #region Security

        services.AddCors(options =>
        {
            options.AddPolicy(name: MyAllowSpecificOrigins,
                builder =>
                {
                    builder
                        .AllowAnyOrigin()
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                });
        });

        //services.AddIdentity<IdentityUser<Guid>, IdentityRole<Guid>>(options =>
        //{
        //    options.Password.RequiredLength = 8;
        //    options.Password.RequireDigit = true;
        //    options.Password.RequireNonAlphanumeric = true;
        //    options.Password.RequireUppercase = true;
        //    options.Password.RequireLowercase = true;
        //})
        //.AddEntityFrameworkStores<IdentityDbContext>()
        //.AddDefaultTokenProviders();

        //var appSettingJwtTokenOptions = Configuration.GetSection(JwtTokenOptions.sectionKey).Get<JwtTokenOptions>();

        //services.AddAuthentication(options =>
        //{
        //    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        //    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        //}).AddJwtBearer(jwtOptions =>
        //{
        //    jwtOptions.TokenValidationParameters = new TokenValidationParameters
        //    {
        //        ValidateIssuer = !string.IsNullOrEmpty(appSettingJwtTokenOptions.Issuer),
        //        ValidIssuer = appSettingJwtTokenOptions.Issuer,

        //        ValidateAudience = !string.IsNullOrEmpty(appSettingJwtTokenOptions.Audience),
        //        ValidAudience = appSettingJwtTokenOptions.Audience,

        //        ValidateIssuerSigningKey = true,
        //        IssuerSigningKey = appSettingJwtTokenOptions.IssuerSigningKey,

        //        RequireExpirationTime = true,
        //        ValidateLifetime = true,

        //        ClockSkew = TimeSpan.Zero
        //    };
        //    jwtOptions.Audience = appSettingJwtTokenOptions.Audience;
        //    jwtOptions.SaveToken = true;
        //    jwtOptions.RequireHttpsMetadata = Environment.IsDevelopment();
        //    jwtOptions.IncludeErrorDetails = !Environment.IsDevelopment();
        //    jwtOptions.Events = new JwtBearerEvents()
        //    {
        //        OnAuthenticationFailed = c =>
        //        {
        //            var response = new BaseResponse()
        //            {
        //                Errors = new List<BaseResponseError>()
        //                    {
        //                        new BaseResponseError()
        //                        {
        //                            Message = Environment.IsDevelopment() ? c.Exception.Message : "An error occured processing your authentication.",
        //                            ErrorCode = "InternalServerError"
        //                        }
        //                    }
        //            };
        //            c.NoResult();
        //            c.Response.StatusCode = 500;
        //            c.Response.ContentType = "application/json";

        //            c.Response.WriteAsync(JsonSerializer.Serialize(response)).GetAwaiter();
        //            return Task.CompletedTask;
        //        }
        //    };
        //});

        //services.AddAuthorization(options =>
        //{
        //    options.AddPolicy("Bearer", new AuthorizationPolicyBuilder()
        //        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        //        .RequireAuthenticatedUser().Build());
        //});

        #endregion

        //services.RegisterServices(Configuration);

        Init(services.BuildServiceProvider());
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(WebApplication app, IWebHostEnvironment env)
    {
        app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Innoku Management v1"));

        app.UseHttpsRedirection();

        app.UseRouting();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseCors(MyAllowSpecificOrigins);
        //app.UseGlobalExceptionHandler();
        //app.UseRequestResponseMiddleware();

        app.Use((context, next) =>
        {
            context.Request.EnableBuffering();
            return next();
        });

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = healthCheck => healthCheck.Tags.Contains("ready")
            });
            endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false
            });
        });
    }

    private void Init(IServiceProvider serviceProvider)
    {
        
    }
}