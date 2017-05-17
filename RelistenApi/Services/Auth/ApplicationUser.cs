﻿using System.Security.Claims;

namespace Relisten.Services.Auth
{
    public class ApplicationUser : ClaimsIdentity
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
