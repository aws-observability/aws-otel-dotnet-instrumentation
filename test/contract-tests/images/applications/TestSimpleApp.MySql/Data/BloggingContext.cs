using Microsoft.EntityFrameworkCore;

namespace TestSimpleApp.MySql.Data;
public class BloggingContext : DbContext
{
    public DbSet<Blog> Blogs { get; set; }
    public DbSet<Post> Posts { get; set; }

    protected readonly IConfiguration Configuration;
    public static readonly string DbType = Environment.GetEnvironmentVariable("DB_TYPE");
    public static readonly string DbHost = Environment.GetEnvironmentVariable("DB_HOST");
    public static readonly string DbUser = Environment.GetEnvironmentVariable("DB_USER");
    public static readonly string DbPass = Environment.GetEnvironmentVariable("DB_PASS");
    public static readonly string DbName = Environment.GetEnvironmentVariable("DB_NAME");

    public BloggingContext()
    {

    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // connect to mysql with connection string from app settings
        var connectionString = String.Format("server={0}; database={1}; user={2}; password={3}", DbHost, DbName, DbUser, DbPass);
        if (DbType == "postgresql")
        {
            connectionString = String.Format("Host={0}; Database={1}; Username={2}; Password={3}", DbHost, DbName, DbUser, DbPass);
            options.UseNpgsql(connectionString);
        }
        else
        {
            options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
        }
    }
}
