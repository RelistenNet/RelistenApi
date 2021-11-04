using System;
using System.Net.Http.Headers;
using System.Text;
using Hangfire.Annotations;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Relisten.Services.Auth
{
    public class MyAuthorizationFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();

            // Allow all authenticated users to see the Dashboard (potentially dangerous).
            return httpContext.User.Identity.IsAuthenticated;
        }
    }

    public class HangfireBasicAuthFilter : IDashboardAuthorizationFilter
    {
        public HangfireBasicAuthFilter(string u, string p)
        {
            Username = u;
            Password = p;
        }

        public string Username { get; set; }
        public string Password { get; set; }

        public bool Authorize([NotNull] DashboardContext dashboardContext)
        {
            var context = dashboardContext.GetHttpContext();
            string header = context.Request.Headers["Authorization"];

            if (string.IsNullOrWhiteSpace(header) == false)
            {
                var authValues = AuthenticationHeaderValue.Parse(header);

                if ("Basic".Equals(authValues.Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    var parameter = Encoding.UTF8.GetString(Convert.FromBase64String(authValues.Parameter));
                    var parts = parameter.Split(':');

                    if (parts.Length > 1)
                    {
                        var login = parts[0];
                        var password = parts[1];

                        if (string.IsNullOrWhiteSpace(login) == false && string.IsNullOrWhiteSpace(password) == false)
                        {
                            return Username == login && Password == password;
                        }
                    }
                }
            }

            return Challenge(context);
        }

        private bool Challenge(HttpContext context)
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"Hangfire Dashboard\"");

            //var buf = Encoding.UTF8.GetBytes("Authentication is required.");
            //context.Response.Body.Write(buf, 0, buf.Length);

            return false;
        }
    }
}
