// Workflow API contracts (Middt.Framework.Model/Model/Workflow/WorkflowModels.cs, camelCase JSON) and a fetch based client
// of the endpoints mapped by `MapMiddtWorkflows` (Middt.Framework.Workflow/Endpoints/WorkflowEndpoints.cs), mirroring
// Middt.Framework.Client/Http/WorkflowApiClient.cs. Like the other clients, failures (ProblemDetails 400/403/404/409,
// network errors) never throw: they come back as `result: -1` with the server messages in `messageList`.

import { ApiClientBase, pathSegment, UNAVAILABLE_MESSAGE } from './client.ts';
import type { ApiClientOptions } from './client.ts';
import { ResultEnum } from './types.ts';
import type { BaseResponseDataModel } from './types.ts';

/** JSON object of workflow data / inputs / schemas. */
export type WorkflowData = Record<string, unknown>;

/** `WorkflowInstanceStatus` (serialized as its name). */
export const WorkflowInstanceStatus = {
  /** Waiting for the next transition (or for a subflow). */
  active: 'Active',
  /** A transition is executing; further transitions are rejected with 409. */
  busy: 'Busy',
  completed: 'Completed',
  /** A task failed; see incidents and use retry. */
  faulted: 'Faulted',
  cancelled: 'Cancelled',
} as const;

export type WorkflowInstanceStatusName = (typeof WorkflowInstanceStatus)[keyof typeof WorkflowInstanceStatus];

/** All statuses in declaration order (status filters). */
export const WORKFLOW_INSTANCE_STATUSES: readonly WorkflowInstanceStatusName[] = Object.freeze(Object.values(WorkflowInstanceStatus));

export interface StartWorkflowRequest {
  /** Optional business key (unique per workflow); starting twice with the same key returns the existing instance. */
  key?: string | null;
  /** Validated against the start transition's input schema. */
  data?: WorkflowData | null;
}

export interface WorkflowSummaryModel {
  key: string;
  version: number;
  title?: string | null;
  icon?: string | null;
}

export interface WorkflowStateModel {
  key: string;
  title?: string | null;
  description?: string | null;
  /** `Start`, `Normal`, `SubFlow`, `Finish`, … */
  type: string;
  /** `None`, `Human`, `Success`, `Error`, `Cancelled`, `Timeout`. */
  subType: string;
  /** Name of the registered view component for this state (server driven UI), if any. */
  view?: string | null;
  /** Key of the JSON layout (server driven UI) shown for this state, if any. */
  layout?: string | null;
  /** Wizard step number (progress indicator), if the state is a step. */
  step?: number | null;
}

export interface WorkflowTransitionModel {
  key: string;
  title?: string | null;
  description?: string | null;
  /** Source state; null for shared transitions. */
  from?: string | null;
  target: string;
  /** `Manual`, `Automatic`, `Scheduled`, … */
  trigger: string;
  /** Optional button style (`primary`, `secondary`, `success`, `warning`, `danger`, `light`). */
  style?: string | null;
  /** Optional Material icon name for transition buttons. */
  icon?: string | null;
}

/** Transition option exposed to AI agents for dynamic state routing, including its key, title, description, and target state. */
export interface WorkflowTransitionOptionModel {
  key: string;
  title?: string | null;
  description?: string | null;
  target?: string | null;
}

/** States and transitions of a workflow (e.g. for diagrams). */
export interface WorkflowDefinitionModel {
  key: string;
  version: number;
  title?: string | null;
  icon?: string | null;
  startState: string;
  startInputSchema?: WorkflowData | null;
  dataSchema?: WorkflowData | null;
  states: WorkflowStateModel[];
  transitions: WorkflowTransitionModel[];
}

/** A manual transition the caller may execute in the instance's current state. */
export interface AvailableTransitionModel {
  key: string;
  title?: string | null;
  description?: string | null;
  target: string;
  /** Optional button style (`primary`, `secondary`, `success`, `warning`, `danger`, `light`). */
  style?: string | null;
  /** Optional Material icon name for transition buttons. */
  icon?: string | null;
  /** Whether the transition is shown as a button in the top instance toolbar (`true` by default). */
  toolbar?: boolean;
  /** JSON Schema of the expected request body, when the transition takes input. */
  inputSchema?: WorkflowData | null;
  /** Name of a registered input form component that replaces the schema generated form, if any. */
  view?: string | null;
  /** Render the input form inside the instance screen (wizard step) instead of a dialog. */
  inline?: boolean;
  /** Data path the input is stored under (lets UIs pre-fill the form when a step is revisited). */
  inputInto?: string | null;
  /**
   * `jsonPatch` when the transition expects an RFC 6902 JSON Patch on the data at `inputInto` (body `[{op, path, value}]`
   * or `{ patch: [...] }`) instead of an object; null for a plain object body. See `workflowTransitionInput`.
   */
  inputFormat?: 'jsonPatch' | null;
}

/** One RFC 6902 JSON Patch operation. */
export interface WorkflowJsonPatchOperation {
  op: 'add' | 'remove' | 'replace' | 'move' | 'copy' | 'test';
  path: string;
  value?: unknown;
  from?: string;
}

export interface ScheduledTransitionModel {
  key: string;
  /** ISO timestamp (UTC). */
  dueAt: string;
}

export interface WorkflowAgentToolCallModel {
  id: string;
  runId: string;
  agentName: string;
  taskKey: string;
  callId: string;
  name: string;
  arguments?: unknown;
  result?: unknown;
  order: number;
  createdAt: string;
}

export interface WorkflowAgentRunModel {
  id: string;
  instanceId: string;
  workflow: string;
  state: string;
  transitionKey?: string | null;
  taskKey: string;
  agentName: string;
  mode: string;
  selectedTransition?: string | null;
  allowedTransitions?: WorkflowTransitionOptionModel[];
  responseId?: string | null;
  finishReason?: string | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  totalTokens?: number | null;
  toolCallCount: number;
  output?: unknown;
  createdAt: string;
  toolCalls?: WorkflowAgentToolCallModel[];
}

export interface WorkflowInstanceModel {
  id: string;
  key?: string | null;
  workflow: string;
  workflowVersion: number;
  state: string;
  stateTitle?: string | null;
  stateType: string;
  stateSubType: string;
  /** Name of the view component of the current state (server driven UI), if any. */
  view?: string | null;
  /** Key of the JSON layout of the current state (rendered with `data` and `instance`), if any. */
  layout?: string | null;
  status: WorkflowInstanceStatusName;
  data: WorkflowData;
  dataVersion: number;
  parentInstanceId?: string | null;
  waitingForInstanceId?: string | null;
  error?: string | null;
  /** Key of the failed transition (or `$background`) when `status` is `Faulted`. */
  pendingTransition?: string | null;
  /** Input payload of the failed transition (or current data when retrying a non-input step) when `status` is `Faulted`. */
  pendingInput?: WorkflowData | null;
  /** JSON Schema for editing `pendingInput` before retrying a `Faulted` instance. */
  pendingInputSchema?: WorkflowData | null;
  startedBy?: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
  availableTransitions: AvailableTransitionModel[];
  scheduledTransitions: ScheduledTransitionModel[];
  /** Projected state and available transitions of the active child subflow when `waitingForInstanceId` is set. */
  activeSubFlow?: WorkflowInstanceModel | null;
  /** Latest output and metadata per AI agent / MCP task (from `WorkflowAgentRuns`). */
  agents?: WorkflowData;
  /** Chronological AI agent and MCP execution runs (from `WorkflowAgentRuns`). */
  agentRuns?: WorkflowAgentRunModel[];
  /** Flattened tool calls across all AI agent and MCP runs (from `WorkflowAgentToolCalls`). */
  toolCalls?: WorkflowAgentToolCallModel[];
}

export interface WorkflowTaskExecutionModel {
  taskKey: string;
  taskType: string;
  hook: string;
  attempts: number;
  durationMs: number;
  outcome: string;
}

export interface WorkflowHistoryModel {
  transitionKey: string;
  fromState?: string | null;
  toState: string;
  trigger: string;
  executedBy?: string | null;
  startedAt: string;
  finishedAt: string;
  dataVersion: number;
  tasks?: WorkflowTaskExecutionModel[];
}

export interface WorkflowIncidentModel {
  id: number;
  state?: string | null;
  transitionKey?: string | null;
  taskKey?: string | null;
  error: string;
  attempts: number;
  createdAt: string;
  resolvedAt?: string | null;
}

export interface WorkflowTaskParamSpecModel {
  name: string;
  label: string;
  type: string;
  required: boolean;
  defaultValue?: string | null;
  options: string[];
  description: string;
}

export interface WorkflowTaskSpecModel {
  name: string;
  title: string;
  category: string;
  icon: string;
  description: string;
  template: WorkflowData;
  params: WorkflowTaskParamSpecModel[];
}

export interface WorkflowValidationResultModel {
  isValid: boolean;
  errors: string[];
}

export interface WorkflowVersionSummaryModel {
  version: number;
  title?: string | null;
  statesCount: number;
  transitionsCount: number;
  isLatest: boolean;
  updatedBy?: string | null;
  updatedAt?: string | null;
}

export interface WorkflowVersionDiffModel {
  key: string;
  fromVersion: number;
  toVersion: number;
  addedStates: string[];
  removedStates: string[];
  changedStates: string[];
  addedTransitions: string[];
  removedTransitions: string[];
  patch: WorkflowJsonPatchOperation[];
}

export interface WorkflowPendingJobModel {
  id: string;
  workflow: string;
  instanceId: string;
  kind: string;
  state: string;
  transitionKey?: string | null;
  dueAt: string;
  attempts: number;
  lastError?: string | null;
}

export interface WorkflowOperationsSummaryModel {
  faultedCount: number;
  busyCount: number;
  pendingJobCount: number;
  unresolvedIncidentCount: number;
  faultedInstances: WorkflowInstanceModel[];
  pendingJobs: WorkflowPendingJobModel[];
  recentIncidents: WorkflowIncidentModel[];
}

export interface WorkflowBatchRetryItemRequest {
  workflow: string;
  instanceId: string;
  input?: WorkflowData | null;
}

export interface WorkflowBatchRetryRequest {
  items: WorkflowBatchRetryItemRequest[];
}

export interface WorkflowBatchRetryFailureModel {
  instanceId: string;
  workflow: string;
  error: string;
}

export interface WorkflowBatchOperationResultModel {
  succeededIds: string[];
  failed: WorkflowBatchRetryFailureModel[];
}

export interface WorkflowInboxItemModel {
  instanceId: string;
  key?: string | null;
  workflow: string;
  workflowVersion: number;
  workflowTitle?: string | null;
  workflowIcon?: string | null;
  state: string;
  stateTitle?: string | null;
  startedBy?: string | null;
  createdAt: string;
  updatedAt: string;
  dueAt?: string | null;
  availableTransitions: AvailableTransitionModel[];
}

export interface WorkflowSimulationStepRequest {
  transition: string;
  input?: WorkflowData | null;
}

export interface WorkflowSimulationRequest {
  key?: string | null;
  version?: number | null;
  definition?: WorkflowData | null;
  data?: WorkflowData | null;
  steps?: WorkflowSimulationStepRequest[];
  taskMocks?: Record<string, WorkflowData> | null;
}

export interface WorkflowSimulationResultModel {
  success: boolean;
  finalState: string;
  finalStatus: WorkflowInstanceStatusName;
  visitedStates: string[];
  executedTransitions: string[];
  finalData: WorkflowData;
  availableTransitions: AvailableTransitionModel[];
  error?: string | null;
}

export interface WorkflowBundleItemModel {
  key: string;
  version: number;
  title?: string | null;
  checksum: string;
  definition: WorkflowData;
}

export interface WorkflowBundleModel {
  exportedAt: string;
  checksum: string;
  items: WorkflowBundleItemModel[];
  layouts: Record<string, WorkflowData>;
}

export interface WorkflowBundleImportRequest {
  dryRun?: boolean;
  expectedChecksum?: string | null;
  items: WorkflowBundleItemModel[];
  layouts?: Record<string, WorkflowData> | null;
}

export interface WorkflowBundleImportResultModel {
  isValid: boolean;
  dryRun: boolean;
  importedWorkflows: string[];
  importedLayouts: string[];
  errors: string[];
}

export interface WorkflowPurgeRequest {
  retentionDays?: number;
  dryRun?: boolean;
  workflow?: string | null;
}

export interface WorkflowPurgeResultModel {
  dryRun: boolean;
  cutoffUtc: string;
  purgedInstancesCount: number;
  purgedHistoryCount: number;
  purgedIncidentsCount: number;
}

/** Filter and paging of `listInstances` (defaults: page 1, 20 per page). */
export interface WorkflowInstanceQuery {
  state?: string | null;
  status?: WorkflowInstanceStatusName | null;
  key?: string | null;
  /** Data property filters (`{ fullName: 'Ayşe', segment: 'Kurumsal' }` → `?data.fullName=…&data.segment=…`). */
  data?: Readonly<Record<string, string | null | undefined>> | null;
  page?: number;
  pageSize?: number;
}

export interface WorkflowApiClientOptions extends ApiClientOptions {
  /** Route prefix of `MapMiddtWorkflows` (default `api/workflows`). */
  routePrefix?: string;
}

const isAbort = (error: unknown): boolean => error instanceof Error && error.name === 'AbortError';

function query(values: Record<string, string | number | null | undefined>): string {
  const parts: string[] = [];
  for (const [name, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== '') {
      parts.push(`${encodeURIComponent(name)}=${encodeURIComponent(String(value))}`);
    }
  }

  return parts.length === 0 ? '' : `?${parts.join('&')}`;
}

/** Client of the workflow REST API mapped by `MapMiddtWorkflows` (default prefix `api/workflows`). */
export class WorkflowApiClient extends ApiClientBase {
  readonly routePrefix: string;

  constructor(options: WorkflowApiClientOptions = {}) {
    super(options);
    this.routePrefix = (options.routePrefix ?? 'api/workflows').replace(/^\/+|\/+$/g, '');
  }

  /** Workflows the user may access (latest versions). */
  listWorkflows(signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowSummaryModel[]>> {
    return this.send('GET', this.routePrefix, undefined, signal);
  }

  /** Catalog of built-in workflow tasks and their parameter specifications for the visual designer. */
  listTaskSpecs(signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowTaskSpecModel[]>> {
    return this.send('GET', `${this.routePrefix}/tasks`, undefined, signal);
  }

  /** Full raw JSON definition of `workflow` for the visual workflow designer. */
  getDefinitionJson(workflow: string, version?: number | null, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowData>> {
    const suffix = version === undefined || version === null ? '' : query({ version: Math.trunc(version) });
    return this.send('GET', `${this.workflowRoute(workflow)}/definition${suffix}`, undefined, signal);
  }

  /** Validates a draft workflow JSON document without saving it. */
  validateDefinition(definition: WorkflowData, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowValidationResultModel>> {
    return this.send('POST', `${this.routePrefix}/validate`, definition, signal);
  }

  /** Validates and saves a workflow JSON definition at runtime (requires `ui-admin` role). */
  saveDefinition(workflow: string, definition: WorkflowData, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowData>> {
    return this.send('PUT', `${this.workflowRoute(workflow)}/definition`, definition, signal);
  }

  /** Lists all immutable versions of `workflow` ordered from newest to oldest. */
  listVersions(workflow: string, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowVersionSummaryModel[]>> {
    return this.send('GET', `${this.workflowRoute(workflow)}/versions`, undefined, signal);
  }

  /** Computes structural and RFC 6902 JSON Patch diff between two versions of `workflow`. */
  getVersionDiff(workflow: string, fromVersion?: number | null, toVersion?: number | null, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowVersionDiffModel>> {
    const suffix = query({
      fromVersion: fromVersion === undefined || fromVersion === null ? undefined : Math.trunc(fromVersion),
      toVersion: toVersion === undefined || toVersion === null ? undefined : Math.trunc(toVersion),
    });
    return this.send('GET', `${this.workflowRoute(workflow)}/diff${suffix}`, undefined, signal);
  }

  /** Publishes an earlier `version` of `workflow` as a new latest version (requires `ui-admin` role). */
  rollbackVersion(workflow: string, version: number, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowData>> {
    return this.send('POST', `${this.workflowRoute(workflow)}/versions/${Math.trunc(version)}/rollback`, undefined, signal);
  }

  /** Operational summary of faulted instances, busy instances, pending jobs, and unresolved incidents. */
  getOperationsSummary(workflow?: string | null, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowOperationsSummaryModel>> {
    const suffix = query({ workflow });
    return this.send('GET', `${this.routePrefix}/operations${suffix}`, undefined, signal);
  }

  /** Retries multiple faulted workflow instances in a batch. */
  retryBatch(request: WorkflowBatchRetryRequest, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowBatchOperationResultModel>> {
    return this.send('POST', `${this.routePrefix}/operations/retry-batch`, request, signal);
  }

  /** Unified human task inbox across all accessible workflows. */
  listInbox(workflow?: string | null, page = 1, pageSize = 20, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowInboxItemModel[]>> {
    const suffix = query({ workflow, page: Math.trunc(page), pageSize: Math.trunc(pageSize) });
    return this.send('GET', `${this.routePrefix}/inbox${suffix}`, undefined, signal);
  }

  /** Simulates a workflow execution in memory without database side effects. */
  simulate(workflow: string, request: WorkflowSimulationRequest = {}, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowSimulationResultModel>> {
    return this.send('POST', `${this.workflowRoute(workflow)}/simulate`, request, signal);
  }

  /** Exports all workflow definitions and UI layouts as a portable bundle with SHA-256 checksums. */
  exportBundle(signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowBundleModel>> {
    return this.send('GET', `${this.routePrefix}/bundle`, undefined, signal);
  }

  /** Validates (`dryRun: true`) or imports (`dryRun: false`) a portable workflow/layout bundle. */
  importBundle(request: WorkflowBundleImportRequest, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowBundleImportResultModel>> {
    return this.send('POST', `${this.routePrefix}/bundle/import`, request, signal);
  }

  /** Purges completed/cancelled workflow instances older than `retentionDays` (requires `ui-admin` role). */
  purge(request: WorkflowPurgeRequest, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowPurgeResultModel>> {
    return this.send('POST', `${this.routePrefix}/maintenance/purge`, request, signal);
  }

  /**
   * Definition of `workflow`: the latest version, or `version` when given (instances keep running on the version they were
   * started with, see `WorkflowInstanceModel.workflowVersion`).
   */
  getDefinition(workflow: string, version?: number | null, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowDefinitionModel>> {
    const suffix = version === undefined || version === null ? '' : query({ version: Math.trunc(version) });
    return this.send('GET', `${this.workflowRoute(workflow)}${suffix}`, undefined, signal);
  }

  /** Starts an instance (201, or 200 with the existing instance when the business key is already used). */
  start(workflow: string, request: StartWorkflowRequest = {}, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowInstanceModel>> {
    return this.send('POST', `${this.workflowRoute(workflow)}/instances`, request, signal);
  }

  /** One page of instances; `count` is the total count. */
  listInstances(workflow: string, filter: WorkflowInstanceQuery = {}, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowInstanceModel[]>> {
    const params: Record<string, string | number | null | undefined> = {
      state: filter.state,
      status: filter.status,
      key: filter.key,
    };
    if (filter.data) {
      for (const [field, value] of Object.entries(filter.data)) {
        const trimmedField = field.trim();
        const trimmedValue = typeof value === 'string' ? value.trim() : '';
        if (trimmedField !== '' && trimmedValue !== '') {
          params[`data.${trimmedField}`] = trimmedValue;
        }
      }
    }
    params['page'] = Math.trunc(filter.page ?? 1);
    params['pageSize'] = Math.trunc(filter.pageSize ?? 20);
    const suffix = query(params);
    return this.send('GET', `${this.workflowRoute(workflow)}/instances${suffix}`, undefined, signal);
  }

  /** Instance including the transitions the current user may execute. */
  getInstance(workflow: string, id: string, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowInstanceModel>> {
    return this.send('GET', this.instanceRoute(workflow, id), undefined, signal);
  }

  /** Executes a manual transition; with `runAsync` the transition is queued (202). */
  executeTransition(
    workflow: string,
    id: string,
    transition: string,
    input?: WorkflowData | null,
    runAsync = false,
    signal?: AbortSignal,
  ): Promise<BaseResponseDataModel<WorkflowInstanceModel>> {
    if (transition === '') {
      throw new Error('Transition key is required.');
    }

    const path = `${this.instanceRoute(workflow, id)}/transitions/${pathSegment(transition, 'Transition key')}${runAsync ? '?async=true' : ''}`;
    return this.send('POST', path, input ?? undefined, signal);
  }

  getHistory(workflow: string, id: string, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowHistoryModel[]>> {
    return this.send('GET', `${this.instanceRoute(workflow, id)}/history`, undefined, signal);
  }

  getIncidents(workflow: string, id: string, signal?: AbortSignal): Promise<BaseResponseDataModel<WorkflowIncidentModel[]>> {
    return this.send('GET', `${this.instanceRoute(workflow, id)}/incidents`, undefined, signal);
  }

  /** Retries the failed step of a `Faulted` instance, optionally with updated input/data. */
  retry(
    workflow: string,
    id: string,
    inputOrSignal?: WorkflowData | AbortSignal | null,
    signal?: AbortSignal,
  ): Promise<BaseResponseDataModel<WorkflowInstanceModel>> {
    const isSig = inputOrSignal instanceof AbortSignal;
    const body = isSig ? undefined : (inputOrSignal ?? undefined);
    const sig = isSig ? inputOrSignal : signal;
    return this.send('POST', `${this.instanceRoute(workflow, id)}/retry`, body, sig);
  }

  /**
   * Server rendered HTML of the current state's view (`GET …/instances/{id}/view`, the Razor view rendered to static HTML).
   * `data` is the HTML fragment; a state without a registered view is a 404 failure. Insert it only through a sanitizer.
   */
  async renderView(workflow: string, id: string, signal?: AbortSignal): Promise<BaseResponseDataModel<string>> {
    const path = `${this.instanceRoute(workflow, id)}/view`; // invalid keys throw (rejected promise), like the other methods
    try {
      const response = await this.sendRaw('GET', path, signal);
      if (!response.ok) {
        return await ApiClientBase.readResponse<BaseResponseDataModel<string>>(response);
      }

      return { result: ResultEnum.success, messageList: [], count: 1, data: await response.text() };
    } catch (error) {
      if (isAbort(error) && signal?.aborted) {
        throw error;
      }

      return { result: ResultEnum.error, messageList: [UNAVAILABLE_MESSAGE], count: 0, data: null };
    }
  }

  protected workflowRoute(workflow: string): string {
    if (workflow === '') {
      throw new Error('Workflow key is required.');
    }

    return `${this.routePrefix}/${pathSegment(workflow, 'Workflow key')}`;
  }

  protected instanceRoute(workflow: string, id: string): string {
    if (id === '') {
      throw new Error('Instance id is required.');
    }

    return `${this.workflowRoute(workflow)}/instances/${pathSegment(id, 'Instance id')}`;
  }
}

/** Declarative AI agent specification in the AI Agent Studio (`/agent-admin`). */
export interface AgentDefinitionModel {
  name: string;
  description?: string | null;
  instructions?: string | null;
  modelId?: string | null;
  temperature?: number | null;
  maxOutputTokens?: number | null;
  maxToolCalls?: number | null;
  maxTotalTokens?: number | null;
  tools?: string[];
  mcpServers?: string[];
  allowedTransitions?: string[];
  outputSchema?: WorkflowData | null;
}

/** Declarative AI tool specification (`expression`, `task`, `http`, or `mcp`). */
export interface AgentToolDefinitionModel {
  name: string;
  description?: string | null;
  kind?: string;
  parameters?: WorkflowData | null;
  task?: string | null;
  config?: WorkflowData | null;
  into?: string | null;
  writeToWorkflowData?: boolean;
  expression?: string | null;
  output?: unknown;
  method?: string | null;
  url?: string | null;
  queryParams?: WorkflowData | null;
  headers?: WorkflowData | null;
  body?: unknown;
  mcpServer?: string | null;
  mcpTool?: string | null;
  roles?: string[];
}

/** Declarative Model Context Protocol (MCP) resource definition. */
export interface McpResourceDefinitionModel {
  uri: string;
  name: string;
  description?: string | null;
  mimeType?: string;
  content?: unknown;
}

/** Declarative Model Context Protocol (MCP) prompt template definition. */
export interface McpPromptDefinitionModel {
  name: string;
  description?: string | null;
  template: string;
}

/** Model Context Protocol (MCP) server configuration. */
export interface McpServerDefinitionModel {
  name: string;
  description?: string | null;
  endpoint?: string | null;
  headers?: Record<string, string>;
  tools?: AgentToolDefinitionModel[];
  resources?: McpResourceDefinitionModel[];
  prompts?: McpPromptDefinitionModel[];
}

/** Complete AI Agent, Declarative Tool, and MCP Server catalog (`GET /api/agents/catalog`). */
export interface AgentCatalogModel {
  provider?: string | null;
  defaultModelId?: string | null;
  useLocalFallback?: boolean;
  hasExternalApiKey?: boolean;
  agents: AgentDefinitionModel[];
  tools: AgentToolDefinitionModel[];
  mcpServers: McpServerDefinitionModel[];
}

/** Validation result for an AI agent, declarative tool, or MCP server catalog (`POST /api/agents/validate`). */
export interface AgentValidationResultModel {
  valid: boolean;
  errors: string[];
}

/** Dry-run playground request for testing an AI agent (`POST /api/agents/test`). */
export interface AgentPlaygroundRequest {
  agent?: string | null;
  definition?: AgentDefinitionModel | null;
  prompt: string;
  data?: WorkflowData | null;
  allowedTransitions?: string[] | null;
}

/** Execution result of an interactive AI agent playground run (`POST /api/agents/test`). */
export interface AgentPlaygroundResultModel {
  agentName: string;
  modelId?: string | null;
  provider?: string | null;
  usedLocalFallback: boolean;
  prompt: string;
  output?: unknown;
  rawText?: string | null;
  selectedTransition?: string | null;
  allowedTransitions: WorkflowTransitionOptionModel[];
  toolCalls: WorkflowAgentToolCallModel[];
  dataAfter: WorkflowData;
  durationMs: number;
  inputTokens?: number | null;
  outputTokens?: number | null;
  totalTokens?: number | null;
  finishReason?: string | null;
  responseId?: string | null;
}

export interface AgentApiClientOptions extends ApiClientOptions {
  /** Route prefix of `MapMiddtAgents` (default `api/agents`). */
  routePrefix?: string;
}

/** Client of the AI Agent Studio & Playground REST API mapped by `MapMiddtAgents` (default prefix `api/agents`). */
export class AgentApiClient extends ApiClientBase {
  readonly routePrefix: string;

  constructor(options: AgentApiClientOptions = {}) {
    super(options);
    this.routePrefix = (options.routePrefix ?? 'api/agents').replace(/^\/+|\/+$/g, '');
  }

  /** Lists all registered AI agent definitions. */
  listAgents(signal?: AbortSignal): Promise<BaseResponseDataModel<AgentDefinitionModel[]>> {
    return this.send('GET', this.routePrefix, undefined, signal);
  }

  /** Loads the full AI agent, declarative tool, and MCP server catalog along with active provider info. */
  getCatalog(signal?: AbortSignal): Promise<BaseResponseDataModel<AgentCatalogModel>> {
    return this.send('GET', `${this.routePrefix}/catalog`, undefined, signal);
  }

  /** Deploys a full or partial catalog (`agents`, `tools`, `mcpServers`) and persists it (requires `ui-admin` role). */
  deployCatalog(catalog: Partial<AgentCatalogModel>, signal?: AbortSignal): Promise<BaseResponseDataModel<string[]>> {
    return this.send('POST', `${this.routePrefix}/catalog`, catalog, signal);
  }

  /** Creates or updates a single AI agent definition (requires `ui-admin` role). */
  saveAgent(agent: AgentDefinitionModel, signal?: AbortSignal): Promise<BaseResponseDataModel<AgentDefinitionModel>> {
    return this.send('PUT', `${this.routePrefix}/${pathSegment(agent.name, 'Agent name')}`, agent, signal);
  }

  /** Deletes an AI agent definition by name (requires `ui-admin` role). */
  deleteAgent(name: string, signal?: AbortSignal): Promise<BaseResponseDataModel<boolean>> {
    return this.send('DELETE', `${this.routePrefix}/${pathSegment(name, 'Agent name')}`, undefined, signal);
  }

  /** Creates or updates a single declarative AI tool (requires `ui-admin` role). */
  saveTool(tool: AgentToolDefinitionModel, signal?: AbortSignal): Promise<BaseResponseDataModel<AgentToolDefinitionModel>> {
    return this.send('PUT', `${this.routePrefix}/tools/${pathSegment(tool.name, 'Tool name')}`, tool, signal);
  }

  /** Deletes a declarative AI tool by name (requires `ui-admin` role). */
  deleteTool(name: string, signal?: AbortSignal): Promise<BaseResponseDataModel<boolean>> {
    return this.send('DELETE', `${this.routePrefix}/tools/${pathSegment(name, 'Tool name')}`, undefined, signal);
  }

  /** Creates or updates a Model Context Protocol (MCP) server configuration (requires `ui-admin` role). */
  saveMcpServer(server: McpServerDefinitionModel, signal?: AbortSignal): Promise<BaseResponseDataModel<McpServerDefinitionModel>> {
    return this.send('PUT', `${this.routePrefix}/mcp-servers/${pathSegment(server.name, 'MCP server name')}`, server, signal);
  }

  /** Deletes an MCP server configuration by name (requires `ui-admin` role). */
  deleteMcpServer(name: string, signal?: AbortSignal): Promise<BaseResponseDataModel<boolean>> {
    return this.send('DELETE', `${this.routePrefix}/mcp-servers/${pathSegment(name, 'MCP server name')}`, undefined, signal);
  }

  /** Validates a draft agent, tool, or catalog JSON object without saving it. */
  validate(body: WorkflowData, signal?: AbortSignal): Promise<BaseResponseDataModel<AgentValidationResultModel>> {
    return this.send('POST', `${this.routePrefix}/validate`, body, signal);
  }

  /** Executes an interactive dry-run playground test against a registered or inline draft AI agent. */
  testPlayground(request: AgentPlaygroundRequest, signal?: AbortSignal): Promise<BaseResponseDataModel<AgentPlaygroundResultModel>> {
    return this.send('POST', `${this.routePrefix}/test`, request, signal);
  }
}
