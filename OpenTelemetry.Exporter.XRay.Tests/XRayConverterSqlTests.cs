using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using OpenTelemetry.Exporter.XRay.Implementation;
using OpenTelemetry.Exporter.XRay.Tests.Model;
using OpenTelemetry.Resources;
using Xunit;

namespace OpenTelemetry.Exporter.XRay.Tests
{
    public partial class XRayConverterAwsTests
    {
        [Fact]
        public void Should_contain_database_url()
        {
            var converter = XRayTest.CreateDefaultConverter();
            var resource = Resource.Empty;
            var activity = new Activity("Test");

            activity.SetTag(XRayConventions.AttributeDbSystem, "mysql");
            activity.SetTag(XRayConventions.AttributeDbName, "customers");
            activity.SetTag(XRayConventions.AttributeDbStatement, "SELECT * FROM user WHERE user_id = ?");
            activity.SetTag(XRayConventions.AttributeDbUser, "readonly_user");
            activity.SetTag(XRayConventions.AttributeDbConnectionString, "mysql://db.example.com:3306");
            activity.SetTag(XRayConventions.AttributeNetPeerName, "db.example.com");
            activity.SetTag(XRayConventions.AttributeNetPeerPort, "3306");

            var segmentDocument = converter.Convert(resource, activity);
            var segment = JsonSerializer.Deserialize<XRaySegment>(segmentDocument);
            Assert.NotNull(segment);
            Assert.NotNull(segment.Sql);
            Assert.Equal("mysql://db.example.com:3306/customers", segment.Sql.Url);
        }

        [Fact]
        public void Should_not_contain_sql_for_non_sql_database()
        {
            var converter = XRayTest.CreateDefaultConverter();
            var resource = Resource.Empty;
            var activity = new Activity("Test");

            activity.SetTag(XRayConventions.AttributeDbSystem, "redis");
            activity.SetTag(XRayConventions.AttributeDbName, "0");
            activity.SetTag(XRayConventions.AttributeDbStatement, "SET key value");
            activity.SetTag(XRayConventions.AttributeDbUser, "readonly_user");
            activity.SetTag(XRayConventions.AttributeDbConnectionString, "redis://db.example.com:3306");
            activity.SetTag(XRayConventions.AttributeNetPeerName, "db.example.com");
            activity.SetTag(XRayConventions.AttributeNetPeerPort, "3306");

            var segmentDocument = converter.Convert(resource, activity);
            var segment = JsonSerializer.Deserialize<XRaySegment>(segmentDocument);
            Assert.NotNull(segment);
            Assert.Null(segment.Sql);
        }

        [Fact]
        public void Should_generate_database_url()
        {
            var converter = XRayTest.CreateDefaultConverter();
            var resource = Resource.Empty;
            var activity = new Activity("Test");

            activity.SetTag(XRayConventions.AttributeDbSystem, "postgresql");
            activity.SetTag(XRayConventions.AttributeDbName, "customers");
            activity.SetTag(XRayConventions.AttributeDbStatement, "SELECT * FROM user WHERE user_id = ?");
            activity.SetTag(XRayConventions.AttributeDbUser, "readonly_user");
            activity.SetTag(XRayConventions.AttributeDbConnectionString, "");
            activity.SetTag(XRayConventions.AttributeNetPeerName, "db.example.com");
            activity.SetTag(XRayConventions.AttributeNetPeerPort, "3306");

            var segmentDocument = converter.Convert(resource, activity);
            var segment = JsonSerializer.Deserialize<XRaySegment>(segmentDocument);
            Assert.NotNull(segment);
            Assert.NotNull(segment.Sql);
            Assert.Equal("localhost/customers", segment.Sql.Url);
        }
    }
}