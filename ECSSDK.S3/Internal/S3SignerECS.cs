﻿/**
 * Copyright 2017 EMC Corporation. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *  http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/
using Amazon.Runtime.Internal.Auth;
using Amazon;
using System.Collections.Generic;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Util;
using System;
using System.Text;
using System.Linq;
using Amazon.Util;

namespace ECSSDK.S3.Internal
{
    public class S3SignerECS : AbstractAWSSigner
    {
        private readonly bool _useSigV4;

        /// <summary>
        /// S3 signer constructor
        /// </summary>
        public S3SignerECS()
        {
            _useSigV4 = AWSConfigsS3.UseSignatureVersion4;
        }

        public override ClientProtocol Protocol
        {
            get { return ClientProtocol.RestProtocol; }
        }

        public override void Sign(IRequest request, IClientConfig clientConfig, RequestMetrics metrics, string awsAccessKeyId, string awsSecretAccessKey)
        {
            var signer = SelectSigner(this, _useSigV4, request, clientConfig);
            var aws4Signer = signer as AWS4Signer;
            var useV4 = aws4Signer != null;

            if (useV4)
            {
                var signingResult = aws4Signer.SignRequest(request, clientConfig, metrics, awsAccessKeyId, awsSecretAccessKey);
                request.Headers[HeaderKeys.AuthorizationHeader] = signingResult.ForAuthorizationHeader;
                if (request.UseChunkEncoding)
                    request.AWS4SignerResult = signingResult;
            }
            else
                SignRequest(request, metrics, awsAccessKeyId, awsSecretAccessKey);
        }

        internal static void SignRequest(IRequest request, RequestMetrics metrics, string awsAccessKeyId, string awsSecretAccessKey)
        {
            request.Headers[HeaderKeys.XAmzDateHeader] = AWSSDKUtils.FormattedCurrentTimestampRFC822;

            var stringToSign = BuildStringToSign(request);
            metrics.AddProperty(Metric.StringToSign, stringToSign);
            var auth = CryptoUtilFactory.CryptoInstance.HMACSign(stringToSign, awsSecretAccessKey, SigningAlgorithm.HmacSHA1);
            var authorization = string.Concat("AWS ", awsAccessKeyId, ":", auth);
            request.Headers[HeaderKeys.AuthorizationHeader] = authorization;
        }

        static string BuildStringToSign(IRequest request)
        {
            var sb = new StringBuilder("", 256);

            sb.Append(request.HttpMethod);
            sb.Append("\n");

            var headers = request.Headers;
            var parameters = request.Parameters;

            if (headers != null)
            {
                string value = null;
                if (headers.ContainsKey(HeaderKeys.ContentMD5Header) && !String.IsNullOrEmpty(value = headers[HeaderKeys.ContentMD5Header]))
                {
                    sb.Append(value);
                }
                sb.Append("\n");

                if (parameters.ContainsKey("ContentType"))
                {
                    sb.Append(parameters["ContentType"]);
                }
                else if (headers.ContainsKey(HeaderKeys.ContentTypeHeader))
                {
                    sb.Append(headers[HeaderKeys.ContentTypeHeader]);
                }
                sb.Append("\n");
            }
            else
            {
                // The headers are null, but we still need to append
                // the 2 newlines that are required by S3.
                // Without these, S3 rejects the signature.
                sb.Append("\n\n");
            }

            if (parameters.ContainsKey("Expires"))
            {
                sb.Append(parameters["Expires"]);
                if (headers != null)
                    headers.Remove(HeaderKeys.XAmzDateHeader);
            }

            sb.Append("\n");
            sb.Append(BuildCanonicalizedHeaders(headers));

            var canonicalizedResource = BuildCanonicalizedResource(request);
            if (!string.IsNullOrEmpty(canonicalizedResource))
                sb.Append(canonicalizedResource);

            return sb.ToString();
        }

        static string BuildCanonicalizedHeaders(IDictionary<string, string> headers)
        {
            IDictionary<string, string> canonicalizedAmzHeaders = 
                new Dictionary<string, string>();
            foreach (var item in headers)
            {
                var lowerKey = item.Key.ToLowerInvariant();
                if (!lowerKey.StartsWith("x-amz-", StringComparison.Ordinal) &&
                    !lowerKey.StartsWith("x-emc-", StringComparison.Ordinal))
                    continue;
                canonicalizedAmzHeaders.Add(lowerKey, item.Key);
            }
            var sb = new StringBuilder(256);
            foreach (var key in canonicalizedAmzHeaders.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                var pointer = canonicalizedAmzHeaders[key];
                sb.Append(String.Concat(key, ":", headers[pointer], "\n"));
            }
            return sb.ToString();
        }

        private static readonly HashSet<string> SignableParameters = new HashSet<string>
        (
            new[]
            {
                "response-content-type",
                "response-content-language",
                "response-expires",
                "response-cache-control",
                "response-content-disposition",
                "response-content-encoding",
            },
            StringComparer.OrdinalIgnoreCase
        );

        //This is a list of sub resources that S3 does not expect to be signed
        //and thus have to be excluded from the signer. This is only applicable to S3SigV2 signer
        //id:- subresource belongs to analytics,inventory and metrics S3 APIs
        private static readonly HashSet<string> SubResourcesSigningExclusion = new HashSet<string>
        (
            new[]
            {
                "id"
            },
            StringComparer.OrdinalIgnoreCase
        );

        static string BuildCanonicalizedResource(IRequest request)
        {
            // CanonicalResourcePrefix will hold the bucket name if we switched to virtual host addressing
            // during request preprocessing (where it would have been removed from ResourcePath)
            var sb = new StringBuilder(request.CanonicalResourcePrefix);
            sb.Append(!string.IsNullOrEmpty(request.ResourcePath)
                                ? AWSSDKUtils.UrlEncode(request.ResourcePath, true)
                                : "/");

            // form up the set of all subresources and specific query parameters that must be 
            // included in the canonical resource, then append them ordered by key to the 
            // canonicalization
            var resourcesToSign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.SubResources.Count > 0)
            {
                foreach (var subResource in request.SubResources)
                {
                    if (!SubResourcesSigningExclusion.Contains(subResource.Key))
                    {
                        resourcesToSign.Add(subResource.Key, subResource.Value);
                    }
                }
            }

            if (request.Parameters.Count > 0)
            {
                var parameters = request.ParameterCollection.GetSortedParametersList();
                foreach (var parameter in parameters)
                {
                    if (parameter.Value != null && SignableParameters.Contains(parameter.Key))
                    {
                        resourcesToSign.Add(parameter.Key, parameter.Value);
                    }
                }
            }

            var delim = "?";
            List<KeyValuePair<string, string>> resources = new List<KeyValuePair<string, string>>();
            foreach (var kvp in resourcesToSign)
            {
                resources.Add(kvp);
            }

            resources.Sort((firstPair, nextPair) =>
            {
                return string.CompareOrdinal(firstPair.Key, nextPair.Key);
            });

            foreach (var resourceToSign in resources)
            {
                sb.AppendFormat("{0}{1}", delim, resourceToSign.Key);
                if (resourceToSign.Value != null)
                    sb.AppendFormat("={0}", resourceToSign.Value);
                delim = "&";
            }
            return sb.ToString();
        }
    }
}
