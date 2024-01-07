﻿using Microsoft.AspNetCore.Mvc;
using Neo4jClient.Cypher;
using Newtonsoft.Json.Linq;
using NewsFlowAPI.DTOs;
using NewsFlowAPI.Models;
using Neo4jClient;
using NewsFlowAPI.Services;
using StackExchange.Redis;
using Neo4j.Driver;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace NewsFlowAPI.Controllers
{
    public class NewsController : Controller
    {
        private readonly string _newestNewsKey;
        private readonly int _maxLengthOfNewestNews;
        private readonly IConnectionMultiplexer _redis;
        private readonly IBoltGraphClient _neo4j;
        private readonly IIdentifierService _ids;
        private readonly IConfiguration _configuration;
        private readonly IRedisNewsSubscriber _subscriber;
        private readonly IQueryCacheService _queryCache;

        public NewsController(
            IConnectionMultiplexer redis,
            IBoltGraphClient neo4j,
            IIdentifierService ids,
            IConfiguration config,
            IRedisNewsSubscriber subscriber,
            IQueryCacheService queryCache
            )
        {
            _redis = redis;
            _neo4j = neo4j;
            _ids = ids;
            _newestNewsKey = "newestnews";

            _maxLengthOfNewestNews = 20;
            //this.CheckAndInitializeKeysInRedis();
            _configuration = config;
            _subscriber = subscriber;
            _queryCache = queryCache;
        }

       /* public async Task CheckAndInitializeKeysInRedis()
        {
            var db = _redis.GetDatabase();

            if (!db.KeyExists(_trendingNewsKey))
            {
                await db.ListLeftPushAsync(_trendingNewsKey, )
            }

            
        }*/
        
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost("news/createnews")]
        public async Task<ActionResult> CreateNews([FromBody] NewsCreateDTO data)
        {
            try
            {

                //News node
                News news = new News
                {
                    Id = await _ids.NewsNext(),
                    Title = data.Title,
                    Summary = data.Summary,
                    Text = data.Text,
                    ImageUrl = data.ImageUrl,
                    AuthorId = data.authorId,
                    LocationId = data.locationId
                };

                news.PostTime = DateTime.Now;

                await _neo4j.Cypher
                 .Create("(n:News $news)")
                 .WithParam("news", news)
                 .ExecuteWithoutResultsAsync();


                //tags nodes
                /*   var tags = await _neo4j.Cypher
                       .Match("(t:Tag)")
                       .Where((Tag t) => data.tagsIds.Contains(t.Id))
                       .Return(t => t.As<Tag>())
                       .ResultsAsync;
                   var tagsList = tags.ToList();*/

                var tags = await _neo4j.Cypher
                    .Match("(t:Tag)")
                    .Where("any(tagId IN $tagsIds WHERE tagId = t.Id)")
                    .WithParam("tagsIds", data.tagsIds)
                    .Return(t => t.As<Tag>().Id)
                    .ResultsAsync;

                if (tags.Count() == 0)
                {
                    throw new Exception("THERE ISN'T ANY TAG");
                }

                var authorList = await _neo4j.Cypher
                   .Match("(u:User)")
                   .Where((User u) => u.Id == data.authorId && u.Role == "Author")
                   .Return(u => u.As<User>().Id)
                   .ResultsAsync;

                if (authorList.Count() == 0)
                {
                    throw new Exception("THERE ISN'T ANY AUTHOR WITH THAT ID");
                }


                //tagovi
                await _neo4j.Cypher
                 .Match("(n:News), (t:Tag)")
                 .Where("any(tagId IN $tagsIds WHERE tagId = t.Id)")
                 .WithParam("tagsIds", data.tagsIds)
                 .Create("(n)-[:TAGGED]->(t)")
                 .ExecuteWithoutResultsAsync();

                await _neo4j.Cypher
                .Match("(n:News), (u:User)")
                .Where((News n, User u) => n.Id == news.Id && data.authorId == u.Id)
                .Create("(n)-[:WRITTEN_BY]->(u)")
                .ExecuteWithoutResultsAsync();

                await _neo4j.Cypher
                    .Match("(n:News), (l:Location)")
                    .Where((News n, Location l) => n.Id == news.Id && l.Id == data.locationId)
                    .Create("(n)-[:LOCATED]->(l)")
                    .ExecuteWithoutResultsAsync();


                //insert in newest
                var db = _redis.GetDatabase();

                //if max length of new news is here, then take out the last one 
                if (db.ListLength(_newestNewsKey) > 20 )
                {
                    db.ListRightPop(_newestNewsKey);
                }

                NewsRedisStorageDTO newsForRedis = new NewsRedisStorageDTO
                {
                    Id = news.Id,
                    Title = news.Title,
                    Summary = news.Summary,
                    Text = news.Text,
                    ImageUrl = news.ImageUrl,
                    authorId = news.AuthorId,
                    locationId = news.LocationId,
                    PostTime = news.PostTime
                };

                db.ListLeftPush(_newestNewsKey, JsonConvert.SerializeObject(newsForRedis));

                return Ok(news);
            }
            catch(Exception e)
            {
                return StatusCode(500, e);
            }
        }

        [HttpGet("news/getAllNews")]
        public async Task<ActionResult> GetAllNews()
        {
            try
            {
                var news = await _neo4j.Cypher
                    .Match("(n:News)")
                    .Return(n => n.As<News>())
                    .ResultsAsync;

                return Ok(news);
            }
            catch (Exception e)
            {
                return StatusCode(500, e);
            }
        }

        [HttpDelete("news/deleteNewsById/{id}")]
        public async Task<ActionResult> DeleteNewsById([FromRoute] int id)
        {
            try
            {

                var newsList = await _neo4j.Cypher
                    .Match("(n:News)")
                    .Where((News n) => n.Id == id)
                    .Return(n => n.As<News>())
                    .ResultsAsync;

                await _neo4j.Cypher
                    .Match("(n:News)")
                    .Where((News n) => n.Id == id)
                    .DetachDelete("n")
                    .ExecuteWithoutResultsAsync();

                var news = newsList.FirstOrDefault();
                NewsRedisStorageDTO newsForRedis = new NewsRedisStorageDTO
                {
                    Id = news.Id,
                    Title = news.Title,
                    Summary = news.Summary,
                    Text = news.Text,
                    ImageUrl = news.ImageUrl,
                    authorId = news.AuthorId,
                    locationId = news.LocationId,
                    PostTime = news.PostTime
                };

                var db = _redis.GetDatabase();
                var serialized = JsonConvert.SerializeObject(newsForRedis);
                //Deletes from newest if it's there
                long count = db.ListRemove(_newestNewsKey, serialized);

                return Ok(count);
            }
            catch (Exception e)
            {
                return StatusCode(500, e);
            }
        }

        [HttpDelete("news/deleteAllNews")]
        public async Task<ActionResult> DeleteAllNews()
        {
            try
            {
                await _neo4j.Cypher
                    .Match("(n:News)")
                    .DetachDelete("n")
                    .ExecuteWithoutResultsAsync();

                return Ok();
            }
            catch (Exception e)
            {
                return StatusCode(500, e);
            }
        }

        [HttpGet("news/geteNewsById/{authorName}")]
        public async Task<ActionResult> GetNewsByAuthor([FromRoute] string authorName)
        {
            try
            {
                var news = (await _neo4j.Cypher
                    .Match("(n:News)<-[:HAS_WRITTEN]-(u:User)")
                    .Where((User u) => u.Name == authorName)
                    .Return(n => n.As<News>())
                    .ResultsAsync)
                    .ToList();

                return Ok(news.ToList());
            }
            catch (Exception e)
            {
                return StatusCode(500, e);
            }
        }


        [HttpPost("news/geteNewsByTags")]
        public async Task<ActionResult> GetNewsByTags([FromBody] List<long> tagIds)
        {
            try
            {
                var news = await _neo4j.Cypher
                    .Match("(n:News)-[:TAGGED]->(t:Tag)")
                    .Where("any(tagId IN $tagsIds WHERE tagId = t.Id)")
                    .WithParam("tagsIds", tagIds)
                    .ReturnDistinct(n => n.As<News>())
                    .ResultsAsync;



                var newsReturn = news.Select(p =>
                new NewsReturnDTO
                {
                    Title = p.Title,
                    Text = p.Text,
                    Summary = p.Summary,
                    ImageUrl = p.ImageUrl,
                    authorId = p.AuthorId,
                    locationId = p.LocationId
                });

                return Ok(news.ToList());
            }
            catch (Exception e)
            {
                return StatusCode(500, e);
            }

        }

        //[Authorize]
        [HttpGet("ClickNews/{id}")]
        public async Task<ActionResult> ClickNewsId([FromRoute] long id)
        {
            var db = _redis.GetDatabase();
            var news = db.StringGet($"news:{id}").ToString();
            if (String.IsNullOrEmpty(news))
            {
                var newsNeo = await _neo4j.Cypher
                    .Match("(n:News)")
                    .Where((News n) => n.Id == id)
                    .Return(n => n.As<News>())
                    .Limit(1)
                    .ResultsAsync;

                if (newsNeo.Count() == 0)
                {
                    return NotFound("News Not Found");
                }

                var newsNeoObject = newsNeo.First();
                newsNeoObject.ViewsCount += 1;
                float falloff = float.Parse(_configuration.GetSection("ViewsLastPeriodFalloff").Value);
                newsNeoObject.ViewsLastPeriod = (int)Math.Round(falloff * newsNeoObject.ViewsLastPeriod,0);

                //na net pise da ne moze transaction ako imaju razlicit ttl :(
                db.StringSet($"news:{id}",
                    System.Text.Json.JsonSerializer.Serialize(newsNeoObject),
                    expiry:TimeSpan.FromHours(float.Parse(_configuration.GetSection("NewsInRedisPeriodHours").Value)));
                
                db.StringSet($"newsExpire:{id}","",
                    expiry: TimeSpan.FromHours(0.96*float.Parse(_configuration.GetSection("NewsInRedisPeriodHours").Value)));
                
                _neo4j.Cypher
                    .Match("(n:News)")
                    .Where((News n) => n.Id == id)
                    .Set("n.ViewsCount=$views")
                    .WithParam("views", newsNeoObject.ViewsCount + 1)
                    .ExecuteWithoutResultsAsync();

                _subscriber.Subscribe(_redis, _neo4j);
                _subscriber.AddKey($"newsExpire:{id}");

                return Ok(newsNeoObject);
            }

            News newsObject= JsonConvert.DeserializeObject<News>(news);
            
            newsObject.ViewsLastPeriod += 1;
            newsObject.ViewsCount += 1;
            var updatedValue = JsonConvert.SerializeObject(newsObject);
            db.StringSet($"news:{id}", updatedValue, expiry: db.KeyTimeToLive($"news:{id}"));


            _neo4j.Cypher
                .Match("(n:News)")
                .Where((News n) => n.Id == id)
                .Set("n.ViewsCount=$views")
                .WithParam("views", newsObject.ViewsCount)
                .ExecuteWithoutResultsAsync();

            return Ok(newsObject);
        }
        //[Authorize]
        [HttpPut("LikeNews/{id}")] 
        public async Task<ActionResult> LikeNews([FromRoute] long id)
        {

            var newsNeo = await _neo4j.Cypher
                    .Match("(n:News)")
                    .Where((News n) => n.Id == id)
                    .Return(n => n.As<News>())
                    .Limit(1)
                    .ResultsAsync;
            
            var newsNeoObject = newsNeo.First();

            _neo4j.Cypher
                   .Match("(n:News)")
                   .Where((News n) => n.Id == id)
                   .Set("n.LikeCount=$views")
                   .WithParam("views", newsNeoObject.LikeCount+ 1)
                   .ExecuteWithoutResultsAsync();

            var db = _redis.GetDatabase();
            var news = db.StringGet($"news:{id}").ToString();
            if (!String.IsNullOrEmpty(news))
            {
            News newsObject = JsonConvert.DeserializeObject<News>(news);
            newsObject.LikeCount += 1;
            var updatedValue = JsonConvert.SerializeObject(newsObject);
            db.StringSet($"news:{id}", updatedValue, expiry: db.KeyTimeToLive($"news:{id}"));
               
            }

                return Ok(newsNeoObject.ViewsCount+1);

        }


        //[Authorize]
        [HttpGet("GetTrending")]
        public async Task<ActionResult> GetTrending()
        {
            var db = _redis.GetDatabase();
            var trending = db.StringGet("trending:news:");
            if (string.IsNullOrEmpty(trending))
            {
                string pattern = "news:*";
                List<string> keysList = new List<string>();
                var cursor = default(long);
                do
                {
                    var result = db.Execute("SCAN", cursor.ToString(), "MATCH", pattern, "COUNT", "20");
                    var innerResult = (RedisResult[])result;

                    cursor = long.Parse((string)innerResult[0]);

                    var keys = (string[])innerResult[1];

                    foreach (var key in keys)
                    {
                        keysList.Add(key);
                    }
                } while (cursor != 0);

                List<News> newsList = new List<News>();

                foreach (var key in keysList)
                {
                    var news = db.StringGet(key).ToString();
                    var newsObject = JsonConvert.DeserializeObject<News>(news);
                    newsList.Add(newsObject);
                }

                newsList.Sort(delegate (News n2, News n1) { return (n1.ViewsLastPeriod + n1.LikeCount / 3 - n2.ViewsLastPeriod - n2.LikeCount / 3); });
                db.StringSet("trending:news:", JsonConvert.SerializeObject(newsList.Take(10)), expiry: TimeSpan.FromHours(2));
                return Ok(newsList.Take(10));

            }
            else
            {
                var trendingList = JsonConvert.DeserializeObject<List<News>>(trending);
                return Ok(trendingList.Take(10));
            }
        }
    }
}
