using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Controllers
{
    [Route("api/2/artists")]
    public class ShowsController : RelistenBaseController
    {
        public ShowsController(
            RedisService redis,
            DbService db
        ) : base(redis, db) { }

    }
}
