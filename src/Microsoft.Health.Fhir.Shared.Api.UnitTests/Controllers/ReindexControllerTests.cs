﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using Hl7.Fhir.Model;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Api.Controllers;
using Microsoft.Health.Fhir.Api.Features.ActionResults;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Reindex.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Messages.Reindex;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Api.UnitTests.Controllers
{
    public class ReindexControllerTests
    {
        private ReindexController _reindexEnabledController;
        private IMediator _mediator = Substitute.For<IMediator>();
        private IFhirRequestContextAccessor _fhirRequestContextAccessor = Substitute.For<IFhirRequestContextAccessor>();
        private HttpContext _httpContext = new DefaultHttpContext();
        private static ReindexJobConfiguration _reindexJobConfig = new ReindexJobConfiguration() { Enabled = true };
        private IUrlResolver _urlResolver = Substitute.For<IUrlResolver>();

        public ReindexControllerTests()
        {
            _reindexEnabledController = GetController(_reindexJobConfig);
            var controllerContext = new ControllerContext() { HttpContext = _httpContext };
            _reindexEnabledController.ControllerContext = controllerContext;
            _urlResolver.ResolveOperationResultUrl(Arg.Any<string>(), Arg.Any<string>()).Returns(new System.Uri("https://test.com"));
        }

        public static TheoryData<Parameters> InvalidBody =>
            new TheoryData<Parameters>
            {
                GetParamsResourceWithTooManyParams(),
                GetParamsResourceWithWrongNameParam(),
                null,
            };

        public static TheoryData<Parameters> ValidBody =>
            new TheoryData<Parameters>
            {
                GetValidReindexJobPostBody(2, "patient"),
                GetValidReindexJobPostBody(null, null),
            };

        [Fact]
        public async Task GivenACreateReindexRequest_WhenDisabled_ThenRequestNotValidExceptionShouldBeThrown()
        {
            var reindexController = GetController(new ReindexJobConfiguration() { Enabled = false });

            await Assert.ThrowsAsync<RequestNotValidException>(() => reindexController.CreateReindexJob(null));
        }

        [Fact]
        public async Task GivenAGetReindexRequest_WhenDisabled_ThenRequestNotValidExceptionShouldBeThrown()
        {
            var reindexController = GetController(new ReindexJobConfiguration() { Enabled = false });

            await Assert.ThrowsAsync<RequestNotValidException>(() => reindexController.GetReindexJob("id"));
        }

        [Fact]
        public async Task GivenAGetReindexRequest_WhenJobExists_ThenParammetersResourceReturned()
        {
            _mediator.Send(Arg.Any<GetReindexRequest>()).Returns(Task.FromResult(GetReindexJobResponse()));

            var result = await _reindexEnabledController.GetReindexJob("id");

            await _mediator.Received().Send(
                Arg.Is<GetReindexRequest>(r => r.JobId == "id"), Arg.Any<CancellationToken>());

            var parametersResource = ((FhirResult)result).Result.ResourceInstance as Parameters;
            Assert.Equal("Queued", parametersResource.Parameter[2].Value.ToString());
        }

        [Theory]
        [MemberData(nameof(InvalidBody), MemberType = typeof(ReindexControllerTests))]
        public async Task GivenACreateReindexRequest_WhenInvalidBodySent_ThenRequestNotValidThrown(Parameters body)
        {
            _reindexEnabledController.ControllerContext.HttpContext.Request.Method = HttpMethods.Post;
            await Assert.ThrowsAsync<RequestNotValidException>(() => _reindexEnabledController.CreateReindexJob(body));
        }

        [Theory]
        [MemberData(nameof(ValidBody), MemberType = typeof(ReindexControllerTests))]
        public async Task GivenACreateReindexRequest_WithValidBody_ThenCreateReindexJobCalledWithCorrectParams(Parameters body)
        {
            _reindexEnabledController.ControllerContext.HttpContext.Request.Method = HttpMethods.Post;
            _mediator.Send(Arg.Any<CreateReindexRequest>()).Returns(Task.FromResult(GetCreateReindexResponse()));
            var result = await _reindexEnabledController.CreateReindexJob(body);
            await _mediator.Received().Send(
                Arg.Is<CreateReindexRequest>(
                    r => r.MaximumConcurrency.ToString().Equals(body.Parameter.Find(p => p.Name.Equals(JobRecordProperties.MaximumConcurrency)).Value.ToString())
                && r.Scope == body.Parameter.Find(p => p.Name.Equals(JobRecordProperties.Scope)).Value.ToString()),
                Arg.Any<CancellationToken>());
            _mediator.ClearReceivedCalls();

            var parametersResource = ((FhirResult)result).Result.ResourceInstance as Parameters;
            Assert.Equal("Queued", parametersResource.Parameter[2].Value.ToString());
        }

        private ReindexController GetController(ReindexJobConfiguration reindexConfig)
        {
            var operationConfig = new OperationsConfiguration()
            {
                Reindex = reindexConfig,
            };

            IOptions<OperationsConfiguration> optionsOperationConfiguration = Substitute.For<IOptions<OperationsConfiguration>>();
            optionsOperationConfiguration.Value.Returns(operationConfig);

            return new ReindexController(
                _mediator,
                _fhirRequestContextAccessor,
                optionsOperationConfiguration,
                _urlResolver,
                NullLogger<ReindexController>.Instance);
        }

        private static CreateReindexResponse GetCreateReindexResponse()
        {
            var jobRecord = new ReindexJobRecord("hash", 5, "patient");
            var jobWrapper = new ReindexJobWrapper(
                jobRecord,
                WeakETag.FromVersionId("33a64df551425fcc55e4d42a148795d9f25f89d4"));
            return new CreateReindexResponse(jobWrapper);
        }

        private static GetReindexResponse GetReindexJobResponse()
        {
            var jobRecord = new ReindexJobRecord("hash", 5, "patient");
            var jobWrapper = new ReindexJobWrapper(
                jobRecord,
                WeakETag.FromVersionId("33a64df551425fcc55e4d42a148795d9f25f89d4"));
            return new GetReindexResponse(System.Net.HttpStatusCode.OK, jobWrapper);
        }

        private static Parameters GetValidReindexJobPostBody(int? maxConcurrency, string scope)
        {
            var parametersResource = new Parameters();
            parametersResource.Parameter = new List<Parameters.ParameterComponent>();

            parametersResource.Parameter.Add(new Parameters.ParameterComponent()
                { Name = JobRecordProperties.MaximumConcurrency, Value = new FhirDecimal(maxConcurrency ?? _reindexJobConfig.DefaultMaximumThreadsPerReindexJob) });
            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = JobRecordProperties.Scope, Value = new FhirString(scope) });

            return parametersResource;
        }

        private static Parameters GetParamsResourceWithWrongNameParam()
        {
            var parametersResource = new Parameters();
            parametersResource.Parameter = new List<Parameters.ParameterComponent>();

            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = "foo", Value = new FhirDecimal(5) });
            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = JobRecordProperties.Scope, Value = new FhirString("scope") });

            return parametersResource;
        }

        private static Parameters GetParamsResourceWithTooManyParams()
        {
            var parametersResource = new Parameters();
            parametersResource.Parameter = new List<Parameters.ParameterComponent>();

            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = JobRecordProperties.MaximumConcurrency, Value = new FhirDecimal(5) });
            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = JobRecordProperties.Scope, Value = new FhirString("scope") });
            parametersResource.Parameter.Add(new Parameters.ParameterComponent() { Name = "foo", Value = new FhirDecimal(5) });

            return parametersResource;
        }
    }
}
