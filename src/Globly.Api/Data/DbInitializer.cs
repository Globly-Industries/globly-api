using Globly.Api.Auth;
using Globly.Api.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Globly.Api.Data;

public static class DbInitializer
{
    /// <summary>Applies pending migrations, creates roles and the bootstrap admin (Admin:UserName / Admin:Password).</summary>
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DbInitializer));

        await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.Admin))
            await roles.CreateAsync(new IdentityRole(Roles.Admin));

        var adminName = config["Admin:UserName"];
        var adminPassword = config["Admin:Password"];
        if (string.IsNullOrWhiteSpace(adminName) || string.IsNullOrWhiteSpace(adminPassword))
            return;

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var admin = await users.FindByNameAsync(adminName);
        if (admin is null)
        {
            admin = new AppUser { UserName = adminName };
            var result = await users.CreateAsync(admin, adminPassword);
            if (!result.Succeeded)
            {
                logger.LogError("Could not create admin user: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }
            logger.LogInformation("Created admin user {UserName}", adminName);
        }

        if (!await users.IsInRoleAsync(admin, Roles.Admin))
            await users.AddToRoleAsync(admin, Roles.Admin);
    }
}
