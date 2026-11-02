import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute, Router } from '@angular/router';
import {
  isSuccess,
  type AgentCatalogModel,
  type AgentDefinitionModel,
  type AgentPlaygroundResultModel,
  type AgentToolDefinitionModel,
  type McpPromptDefinitionModel,
  type McpResourceDefinitionModel,
  type McpServerDefinitionModel,
  type WorkflowData,
} from '@middt/ui-core';
import { AGENT_API_CLIENT } from './workflow-client';

/**
 * Visual AI Agent, Declarative Tool & MCP Server Studio with Live Test Playground (`<middt-agent-designer />`).
 */
@Component({
  selector: 'middt-agent-designer',
  imports: [MatCardModule, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="middt-wf-studio" data-testid="agent-designer">
      <mat-card appearance="outlined">
        <mat-card-content>
          <div class="middt-ui-toolbar" style="justify-content: space-between; flex-wrap: wrap; gap: .5rem">
            <div class="middt-ui-toolbar" style="flex-wrap: wrap; gap: .5rem">
              <div class="middt-wf-tabs" style="margin: 0; border: 0; padding: 0" role="tablist" aria-label="AI Stüdyo Bölümleri">
                <button
                  type="button"
                  [class]="activeSection() === 'agents' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                  (click)="switchSection('agents')">
                  🤖 AI Ajanları ({{ catalog().agents.length }})
                </button>
                <button
                  type="button"
                  [class]="activeSection() === 'tools' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                  (click)="switchSection('tools')">
                  🛠️ Araçlar ({{ catalog().tools.length }})
                </button>
                <button
                  type="button"
                  [class]="activeSection() === 'mcp' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                  (click)="switchSection('mcp')">
                  🔌 MCP Sunucuları ({{ catalog().mcpServers.length }})
                </button>
              </div>

              @if (catalog().hasExternalApiKey) {
                <span
                  class="middt-ui-badge middt-ui-badge-success"
                  data-testid="agent-provider-badge"
                  title="Harici LLM sağlayıcısı ve API anahtarı aktif">
                  🟢 Sağlayıcı: {{ catalog().provider || 'External' }} ({{ catalog().defaultModelId || 'varsayılan' }} · Canlı API)
                </span>
              } @else {
                <span
                  class="middt-ui-badge middt-ui-badge-warning"
                  data-testid="agent-provider-badge"
                  title="Harici API anahtarı tanımlı değil; deterministik yerel yedekleme aktif">
                  🟡 Sağlayıcı: {{ catalog().provider || 'Local' }} (Deterministik Yerel Yedekleme)
                </span>
              }
            </div>

            <div class="middt-wf-tabs" style="margin: 0; border: 0; padding: 0" role="tablist" aria-label="Editör Görünümü">
              <button
                type="button"
                [class]="editorMode() === 'visual' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                (click)="switchEditorMode('visual')">
                🎨 Görsel Editör
              </button>
              <button
                type="button"
                [class]="editorMode() === 'json' ? 'middt-wf-tab middt-wf-tab-active' : 'middt-wf-tab'"
                (click)="switchEditorMode('json')">
                &#123; &#125; JSON Kodu
              </button>
              <button
                type="button"
                class="middt-wf-tab"
                title="İş Akışı Tasarımcısını Aç"
                (click)="openWorkflowDesigner()">
                🔀 Akış Tasarımcısı
              </button>
            </div>

            <div class="middt-ui-toolbar">
              <button mat-stroked-button type="button" (click)="createNewItem()">
                <mat-icon>add</mat-icon> Yeni Ekle
              </button>
              <button mat-stroked-button type="button" (click)="validateCurrent()">
                <mat-icon>rule</mat-icon> Doğrula
              </button>
              <button mat-flat-button color="primary" type="button" [disabled]="busy()" (click)="saveCurrent()">
                <mat-icon>save</mat-icon> Kaydet
              </button>
              @if (!isNewItem()) {
                <button mat-stroked-button color="warn" type="button" [disabled]="busy()" (click)="deleteCurrent()">
                  <mat-icon>delete</mat-icon> Sil
                </button>
              }
            </div>
          </div>

          @if (errors().length > 0) {
            <div class="middt-ui-alert middt-ui-alert-danger" style="margin-top: .75rem" role="alert">
              <strong>{{ errors().length }} hata:</strong>
              <ul class="middt-ui-list">
                @for (err of errors(); track err) {
                  <li>{{ err }}</li>
                }
              </ul>
            </div>
          } @else if (statusMessage(); as msg) {
            <div class="middt-ui-alert middt-ui-alert-success" style="margin-top: .75rem" role="status">{{ msg }}</div>
          }
        </mat-card-content>
      </mat-card>

      <div
        class="middt-ui-admin"
        [style.grid-template-columns]="activeSection() === 'agents' ? 'minmax(240px, 290px) 1fr minmax(360px, 460px)' : 'minmax(240px, 290px) 1fr'"
        style="gap: 1rem; margin-top: .75rem">
        <!-- 1. Left Catalog Sidebar -->
        <mat-card appearance="outlined">
          <mat-card-content>
            <div style="display: flex; flex-direction: column; gap: .5rem">
              <input
                class="middt-wf-input"
                placeholder="Katalogda ara..."
                [value]="searchFilter()"
                (input)="searchFilter.set($any($event.target).value)"
                aria-label="Katalogda ara" />

              @if (activeSection() === 'agents') {
                <ul class="middt-ui-admin-items" aria-label="AI Ajanları">
                  @for (ag of filteredAgents(); track ag.name) {
                    <li>
                      <button
                        type="button"
                        [class]="selectedAgentName() === ag.name ? 'middt-ui-admin-item middt-ui-admin-item-active' : 'middt-ui-admin-item'"
                        (click)="selectAgent(ag.name)">
                        <strong>🤖 {{ ag.name }}</strong>
                        <small>{{ ag.description || 'Açıklama yok' }}</small>
                        <small>🛠️ {{ (ag.tools ?? []).length }} araç · 🔌 {{ (ag.mcpServers ?? []).length }} MCP</small>
                      </button>
                    </li>
                  }
                </ul>
              } @else if (activeSection() === 'tools') {
                <ul class="middt-ui-admin-items" aria-label="Declarative Araçlar">
                  @for (tl of filteredTools(); track tl.name) {
                    <li>
                      <button
                        type="button"
                        [class]="selectedToolName() === tl.name ? 'middt-ui-admin-item middt-ui-admin-item-active' : 'middt-ui-admin-item'"
                        (click)="selectTool(tl.name)">
                        <strong>🛠️ {{ tl.name }}</strong>
                        <small>Tür: {{ tl.task ? 'task (' + tl.task + ')' : (tl.kind || 'expression') }}</small>
                        <small>{{ tl.description || 'Açıklama yok' }}</small>
                      </button>
                    </li>
                  }
                </ul>
              } @else {
                <ul class="middt-ui-admin-items" aria-label="MCP Sunucuları">
                  @for (srv of filteredMcpServers(); track srv.name) {
                    <li>
                      <button
                        type="button"
                        [class]="selectedMcpName() === srv.name ? 'middt-ui-admin-item middt-ui-admin-item-active' : 'middt-ui-admin-item'"
                        (click)="selectMcpServer(srv.name)">
                        <strong>🔌 {{ srv.name }}</strong>
                        <small>{{ srv.endpoint || 'Yerel (In-Process)' }}</small>
                        <small>🛠️ {{ (srv.tools ?? []).length }} araç · 📄 {{ (srv.resources ?? []).length }} kaynak</small>
                      </button>
                    </li>
                  }
                </ul>
              }
            </div>
          </mat-card-content>
        </mat-card>

        <!-- 2. Center Visual / JSON Inspector -->
        <mat-card appearance="outlined">
          <mat-card-content>
            @if (editorMode() === 'json') {
              <div style="display: flex; flex-direction: column; gap: .5rem">
                <div style="display: flex; justify-content: space-between; align-items: center">
                  <strong>JSON Tanımı ({{ currentItemTitle() }})</strong>
                  <button mat-stroked-button type="button" (click)="applyRawJsonToDraft()">
                    <mat-icon>check</mat-icon> JSON'dan Forma Uygula
                  </button>
                </div>
                <textarea
                  class="middt-wf-input middt-wf-code"
                  rows="24"
                  style="font-family: monospace; font-size: .84rem"
                  aria-label="JSON Tanımı"
                  [value]="rawJsonText()"
                  (input)="rawJsonText.set($any($event.target).value)"></textarea>
              </div>
            } @else if (activeSection() === 'agents') {
              <div style="display: flex; flex-direction: column; gap: .75rem">
                <h3 style="margin: 0; font-size: 1.05rem">
                  🤖 AI Ajan Yapılandırması ({{ draftAgent().name || 'Yeni Ajan' }})
                </h3>

                <div style="display: grid; grid-template-columns: 1fr 1fr; gap: .75rem">
                  <div>
                    <label class="middt-wf-label" for="ag-name">Ajan Adı (name) *</label>
                    <input
                      id="ag-name"
                      class="middt-wf-input"
                      placeholder="örn. dispute-orchestrator"
                      [value]="draftAgent().name"
                      (input)="updateAgentField('name', $any($event.target).value)" />
                  </div>
                  <div>
                    <label class="middt-wf-label" for="ag-model">Model Kimliği (modelId)</label>
                    <input
                      id="ag-model"
                      class="middt-wf-input"
                      [placeholder]="'Varsayılan: ' + (catalog().defaultModelId || 'deepseek-chat')"
                      [value]="draftAgent().modelId ?? ''"
                      (input)="updateAgentField('modelId', $any($event.target).value || null)" />
                  </div>
                </div>

                <div>
                  <label class="middt-wf-label" for="ag-desc">Açıklama (description)</label>
                  <input
                    id="ag-desc"
                    class="middt-wf-input"
                    placeholder="Ajanın görevini ve çoklu-ajan devir (handoff) amacını açıklayın"
                    [value]="draftAgent().description ?? ''"
                    (input)="updateAgentField('description', $any($event.target).value || null)" />
                </div>

                <div style="display: grid; grid-template-columns: repeat(4, 1fr); gap: .75rem">
                  <div>
                    <label class="middt-wf-label" for="ag-temp">Sıcaklık (0-2)</label>
                    <input
                      id="ag-temp"
                      type="number"
                      step="0.1"
                      min="0"
                      max="2"
                      class="middt-wf-input"
                      [value]="draftAgent().temperature ?? ''"
                      (input)="updateAgentNumber('temperature', $any($event.target).value)" />
                  </div>
                  <div>
                    <label class="middt-wf-label" for="ag-max-out">Maks. Çıktı Token</label>
                    <input
                      id="ag-max-out"
                      type="number"
                      min="1"
                      class="middt-wf-input"
                      [value]="draftAgent().maxOutputTokens ?? ''"
                      (input)="updateAgentNumber('maxOutputTokens', $any($event.target).value)" />
                  </div>
                  <div>
                    <label class="middt-wf-label" for="ag-max-tools">Maks. Araç Çağrısı</label>
                    <input
                      id="ag-max-tools"
                      type="number"
                      min="1"
                      class="middt-wf-input"
                      [value]="draftAgent().maxToolCalls ?? ''"
                      (input)="updateAgentNumber('maxToolCalls', $any($event.target).value)" />
                  </div>
                  <div>
                    <label class="middt-wf-label" for="ag-max-tokens">Maks. Toplam Token</label>
                    <input
                      id="ag-max-tokens"
                      type="number"
                      min="1"
                      class="middt-wf-input"
                      [value]="draftAgent().maxTotalTokens ?? ''"
                      (input)="updateAgentNumber('maxTotalTokens', $any($event.target).value)" />
                  </div>
                </div>

                <div>
                  <label class="middt-wf-label" for="ag-instructions">
                    Sistem Talimatları (instructions — <code ngNonBindable>{{ data.* }}</code> şablonları desteklenir) *
                  </label>
                  <textarea
                    id="ag-instructions"
                    class="middt-wf-input"
                    rows="5"
                    placeholder="Sen otonom bir iş akışı ajanısın..."
                    [value]="draftAgent().instructions ?? ''"
                    (input)="updateAgentField('instructions', $any($event.target).value)"></textarea>
                </div>

                <div style="display: grid; grid-template-columns: 1fr 1fr; gap: .75rem">
                  <div>
                    <label class="middt-wf-label">Yetkili Araçlar (tools)</label>
                    <div
                      style="border: 1px solid #dee2e6; border-radius: 6px; padding: .5rem; max-height: 150px; overflow-y: auto; display: flex; flex-direction: column; gap: .35rem">
                      @for (toolName of availableToolNames(); track toolName) {
                        <label style="display: flex; align-items: center; gap: .4rem; font-size: .85rem; cursor: pointer">
                          <input
                            type="checkbox"
                            [checked]="hasAgentTool(toolName)"
                            (change)="toggleAgentTool(toolName, $any($event.target).checked)" />
                          <span><code>{{ toolName }}</code></span>
                        </label>
                      }
                    </div>
                  </div>

                  <div>
                    <label class="middt-wf-label">Bağlı MCP Sunucuları (mcpServers)</label>
                    <div
                      style="border: 1px solid #dee2e6; border-radius: 6px; padding: .5rem; max-height: 150px; overflow-y: auto; display: flex; flex-direction: column; gap: .35rem">
                      @for (srv of catalog().mcpServers; track srv.name) {
                        <label style="display: flex; align-items: center; gap: .4rem; font-size: .85rem; cursor: pointer">
                          <input
                            type="checkbox"
                            [checked]="hasAgentMcpServer(srv.name)"
                            (change)="toggleAgentMcpServer(srv.name, $any($event.target).checked)" />
                          <span><code>{{ srv.name }}</code> ({{ (srv.tools ?? []).length }} araç)</span>
                        </label>
                      }
                    </div>
                  </div>
                </div>

                <div>
                  <label class="middt-wf-label" for="ag-transitions">
                    İzin Verilen İş Akışı Geçişleri (allowedTransitions — virgülle ayrılmış, opsiyonel)
                  </label>
                  <input
                    id="ag-transitions"
                    class="middt-wf-input"
                    placeholder="örn. route-auto-refund, route-specialist-review, route-fraud-investigation"
                    [value]="allowedTransitionsCsv()"
                    (input)="onAllowedTransitionsInput($any($event.target).value)" />
                </div>

                <div>
                  <label class="middt-wf-label" for="ag-schema">Yapılandırılmış Çıktı Şeması (outputSchema — JSON Schema)</label>
                  <textarea
                    id="ag-schema"
                    class="middt-wf-input middt-wf-code"
                    rows="8"
                    style="font-family: monospace; font-size: .82rem"
                    [value]="agentOutputSchemaText()"
                    (input)="agentOutputSchemaText.set($any($event.target).value)"></textarea>
                </div>
              </div>
            } @else if (activeSection() === 'tools') {
              <div style="display: flex; flex-direction: column; gap: .75rem">
                <h3 style="margin: 0; font-size: 1.05rem">
                  🛠️ Declarative Araç Yapılandırması ({{ draftTool().name || 'Yeni Araç' }})
                </h3>

                <div style="display: grid; grid-template-columns: 1fr 1fr 1fr; gap: .75rem">
                  <div>
                    <label class="middt-wf-label" for="tl-name">Araç Adı (name) *</label>
                    <input
                      id="tl-name"
                      class="middt-wf-input"
                      placeholder="örn. evaluate_dispute_policy"
                      [value]="draftTool().name"
                      (input)="updateToolField('name', $any($event.target).value)" />
                  </div>
                  <div>
                    <label class="middt-wf-label" for="tl-kind">Çalışma Türü (kind)</label>
                    <select
                      id="tl-kind"
                      class="middt-wf-input"
                      [value]="draftTool().kind || 'expression'"
                      (change)="updateToolField('kind', $any($event.target).value)">
                      <option value="expression">expression (İfade / Şablon)</option>
                      <option value="task">task (IWorkflowTask Köprüsü)</option>
                      <option value="http">http (HTTP Çağrısı)</option>
                      <option value="mcp">mcp (MCP Sunucu Aracı)</option>
                    </select>
                  </div>
                  <div>
                    <label class="middt-wf-label" for="tl-roles">Yetkili Roller (virgülle)</label>
                    <input
                      id="tl-roles"
                      class="middt-wf-input"
                      placeholder="Boş = tüm roller"
                      [value]="toolRolesCsv()"
                      (input)="onToolRolesInput($any($event.target).value)" />
                  </div>
                </div>

                <div>
                  <label class="middt-wf-label" for="tl-desc">Açıklama (description — LLM bu açıklamaya göre aracı çağırır)</label>
                  <input
                    id="tl-desc"
                    class="middt-wf-input"
                    placeholder="Aracın ne zaman ve hangi parametrelerle çağrılacağını açıklayın"
                    [value]="draftTool().description ?? ''"
                    (input)="updateToolField('description', $any($event.target).value || null)" />
                </div>

                @if ((draftTool().kind || 'expression') === 'expression') {
                  <div>
                    <label class="middt-wf-label" for="tl-expr">
                      Tekil İfade (expression — opsiyonel, <code>args.*</code> ve <code>data.*</code> kullanılabilir)
                    </label>
                    <input
                      id="tl-expr"
                      class="middt-wf-input"
                      placeholder="örn. args.amount * 1.2"
                      [value]="draftTool().expression ?? ''"
                      (input)="updateToolField('expression', $any($event.target).value || null)" />
                  </div>
                  <div>
                    <label class="middt-wf-label" for="tl-output">
                      Yapılandırılmış Çıktı Şablonu (output JSON — <code ngNonBindable>{{ args.* }}</code> şablonları)
                    </label>
                    <textarea
                      id="tl-output"
                      class="middt-wf-input middt-wf-code"
                      rows="6"
                      style="font-family: monospace; font-size: .82rem"
                      [value]="toolOutputText()"
                      (input)="toolOutputText.set($any($event.target).value)"></textarea>
                  </div>
                } @else if (draftTool().kind === 'task') {
                  <div style="display: grid; grid-template-columns: 1fr 1fr 1fr; gap: .75rem; align-items: end">
                    <div>
                      <label class="middt-wf-label" for="tl-task">İş Akışı Görevi (task) *</label>
                      <select
                        id="tl-task"
                        class="middt-wf-input"
                        [value]="draftTool().task || 'set'"
                        (change)="updateToolField('task', $any($event.target).value)">
                        <option value="set">set</option>
                        <option value="http">http</option>
                        <option value="cache">cache</option>
                        <option value="operation">operation</option>
                        <option value="publish">publish</option>
                        <option value="rabbitmq">rabbitmq</option>
                        <option value="loadWorkflow">loadWorkflow</option>
                        <option value="queryWorkflow">queryWorkflow</option>
                        <option value="updateWorkflow">updateWorkflow</option>
                        <option value="triggerWorkflow">triggerWorkflow</option>
                        <option value="startWorkflow">startWorkflow</option>
                      </select>
                    </div>
                    <div>
                      <label class="middt-wf-label" for="tl-into">Sonuç Yolu (into)</label>
                      <input
                        id="tl-into"
                        class="middt-wf-input"
                        placeholder="örn. evaluation"
                        [value]="draftTool().into ?? ''"
                        (input)="updateToolField('into', $any($event.target).value || null)" />
                    </div>
                    <label style="display: flex; align-items: center; gap: .4rem; font-size: .86rem; padding-bottom: .4rem; cursor: pointer">
                      <input
                        type="checkbox"
                        [checked]="draftTool().writeToWorkflowData ?? false"
                        (change)="updateToolField('writeToWorkflowData', $any($event.target).checked)" />
                      <span>Akış Verisine Yaz (writeToWorkflowData)</span>
                    </label>
                  </div>
                  <div>
                    <label class="middt-wf-label" for="tl-config">Görev Konfigürasyonu (config JSON)</label>
                    <textarea
                      id="tl-config"
                      class="middt-wf-input middt-wf-code"
                      rows="6"
                      style="font-family: monospace; font-size: .82rem"
                      [value]="toolConfigText()"
                      (input)="toolConfigText.set($any($event.target).value)"></textarea>
                  </div>
                } @else if (draftTool().kind === 'http') {
                  <div style="display: grid; grid-template-columns: 140px 1fr; gap: .75rem">
                    <div>
                      <label class="middt-wf-label" for="tl-method">HTTP Metodu</label>
                      <select
                        id="tl-method"
                        class="middt-wf-input"
                        [value]="draftTool().method || 'GET'"
                        (change)="updateToolField('method', $any($event.target).value)">
                        <option value="GET">GET</option>
                        <option value="POST">POST</option>
                        <option value="PUT">PUT</option>
                        <option value="DELETE">DELETE</option>
                      </select>
                    </div>
                    <div>
                      <label class="middt-wf-label" for="tl-url">Hedef URL Şablonu (url) *</label>
                      <input
                        id="tl-url"
                        class="middt-wf-input"
                        [placeholder]="'http://mockoon:3000/api/registry/{{ args.taxId }}'"
                        [value]="draftTool().url ?? ''"
                        (input)="updateToolField('url', $any($event.target).value || null)" />
                    </div>
                  </div>
                } @else if (draftTool().kind === 'mcp') {
                  <div style="display: grid; grid-template-columns: 1fr 1fr; gap: .75rem">
                    <div>
                      <label class="middt-wf-label" for="tl-mcp-srv">MCP Sunucu Adı (mcpServer) *</label>
                      <input
                        id="tl-mcp-srv"
                        class="middt-wf-input"
                        placeholder="banking-mcp"
                        [value]="draftTool().mcpServer ?? ''"
                        (input)="updateToolField('mcpServer', $any($event.target).value || null)" />
                    </div>
                    <div>
                      <label class="middt-wf-label" for="tl-mcp-tool">MCP Araç Adı (mcpTool)</label>
                      <input
                        id="tl-mcp-tool"
                        class="middt-wf-input"
                        placeholder="lookup_chargeback_history"
                        [value]="draftTool().mcpTool ?? ''"
                        (input)="updateToolField('mcpTool', $any($event.target).value || null)" />
                    </div>
                  </div>
                }

                <div>
                  <label class="middt-wf-label" for="tl-params">Girdi Parametre Şeması (parameters — JSON Schema)</label>
                  <textarea
                    id="tl-params"
                    class="middt-wf-input middt-wf-code"
                    rows="6"
                    style="font-family: monospace; font-size: .82rem"
                    [value]="toolParametersText()"
                    (input)="toolParametersText.set($any($event.target).value)"></textarea>
                </div>
              </div>
            } @else {
              <div style="display: flex; flex-direction: column; gap: .75rem">
                <h3 style="margin: 0; font-size: 1.05rem">
                  🔌 MCP Sunucu Yapılandırması ({{ draftMcp().name || 'Yeni MCP Sunucusu' }})
                </h3>

                <div style="display: grid; grid-template-columns: 1fr 1fr; gap: .75rem">
                  <div>
                    <label class="middt-wf-label" for="mcp-name">Sunucu Adı (name) *</label>
                    <input
                      id="mcp-name"
                      class="middt-wf-input"
                      placeholder="örn. banking-mcp"
                      [value]="draftMcp().name"
                      (input)="updateMcpField('name', $any($event.target).value)" />
                  </div>
                  <div>
                    <label class="middt-wf-label" for="mcp-endpoint">Uzak JSON-RPC Endpoint (boşsa yerel çalışır)</label>
                    <input
                      id="mcp-endpoint"
                      class="middt-wf-input"
                      placeholder="https://mcp.ornek.com/rpc"
                      [value]="draftMcp().endpoint ?? ''"
                      (input)="updateMcpField('endpoint', $any($event.target).value || null)" />
                  </div>
                </div>

                <div>
                  <label class="middt-wf-label" for="mcp-desc">Açıklama (description)</label>
                  <input
                    id="mcp-desc"
                    class="middt-wf-input"
                    [value]="draftMcp().description ?? ''"
                    (input)="updateMcpField('description', $any($event.target).value || null)" />
                </div>

                <div>
                  <label class="middt-wf-label" for="mcp-tools-json">MCP Araçları (tools JSON Dizisi)</label>
                  <textarea
                    id="mcp-tools-json"
                    class="middt-wf-input middt-wf-code"
                    rows="7"
                    style="font-family: monospace; font-size: .82rem"
                    [value]="mcpToolsText()"
                    (input)="mcpToolsText.set($any($event.target).value)"></textarea>
                </div>

                <div>
                  <label class="middt-wf-label" for="mcp-res-json">MCP Kaynakları (resources JSON Dizisi)</label>
                  <textarea
                    id="mcp-res-json"
                    class="middt-wf-input middt-wf-code"
                    rows="5"
                    style="font-family: monospace; font-size: .82rem"
                    [value]="mcpResourcesText()"
                    (input)="mcpResourcesText.set($any($event.target).value)"></textarea>
                </div>

                <div>
                  <label class="middt-wf-label" for="mcp-prompts-json">MCP İstem Şablonları (prompts JSON Dizisi)</label>
                  <textarea
                    id="mcp-prompts-json"
                    class="middt-wf-input middt-wf-code"
                    rows="4"
                    style="font-family: monospace; font-size: .82rem"
                    [value]="mcpPromptsText()"
                    (input)="mcpPromptsText.set($any($event.target).value)"></textarea>
                </div>
              </div>
            }
          </mat-card-content>
        </mat-card>

        <!-- 3. Right Interactive AI Agent Playground -->
        @if (activeSection() === 'agents') {
          <mat-card appearance="outlined" data-testid="agent-playground">
            <mat-card-content>
              <div style="display: flex; flex-direction: column; gap: .65rem">
                <div style="display: flex; justify-content: space-between; align-items: center">
                  <h3 style="margin: 0; font-size: 1.02rem">▶️ Canlı Test Playground</h3>
                  <button
                    mat-flat-button
                    color="primary"
                    type="button"
                    [disabled]="playgroundBusy()"
                    (click)="runPlayground()"
                    data-testid="agent-playground-run">
                    <mat-icon>play_arrow</mat-icon> Çalıştır
                  </button>
                </div>

                <div>
                  <label class="middt-wf-label" for="pg-preset">Hazır Test Senaryosu</label>
                  <select id="pg-preset" class="middt-wf-input" (change)="applyPlaygroundPreset($any($event.target).value)">
                    <option value="">-- Senaryo Seçin veya Elle Girin --</option>
                    <option value="dispute-low">İtiraz Analizi — Düşük Tutar (Otomatik İade)</option>
                    <option value="dispute-fraud">İtiraz Analizi — Yetkisiz İşlem (Fraud Eskalasyonu)</option>
                    <option value="dispute-specialist">İtiraz Uzman Copilot — Tazminat Hesaplama</option>
                    <option value="credit-risk">Ticari Limit &amp; Risk Değerlendirmesi</option>
                    <option value="invoice-audit">Fatura Ödeme &amp; Mutabakat Denetimi</option>
                  </select>
                </div>

                <div>
                  <label class="middt-wf-label" for="pg-prompt">İstem Metni (prompt) *</label>
                  <textarea
                    id="pg-prompt"
                    class="middt-wf-input"
                    rows="3"
                    placeholder="Ajanın gerçekleştirmesini istediğiniz görevi yazın..."
                    [value]="playgroundPrompt()"
                    (input)="playgroundPrompt.set($any($event.target).value)"></textarea>
                </div>

                <div>
                  <label class="middt-wf-label" for="pg-data">Örnek İş Akışı Verisi (data JSON)</label>
                  <textarea
                    id="pg-data"
                    class="middt-wf-input middt-wf-code"
                    rows="5"
                    style="font-family: monospace; font-size: .8rem"
                    [value]="playgroundDataText()"
                    (input)="playgroundDataText.set($any($event.target).value)"></textarea>
                </div>

                @if (playgroundError(); as pgErr) {
                  <div class="middt-ui-alert middt-ui-alert-danger" role="alert">{{ pgErr }}</div>
                }

                @if (playgroundResult(); as pgRes) {
                  <div
                    style="border-top: 1px solid #dee2e6; padding-top: .65rem; display: flex; flex-direction: column; gap: .55rem"
                    data-testid="agent-playground-result">
                    <div style="display: flex; flex-wrap: wrap; gap: .35rem">
                      <span class="middt-ui-badge middt-ui-badge-info">⏱️ {{ pgRes.durationMs }} ms</span>
                      <span
                        [class]="pgRes.usedLocalFallback ? 'middt-ui-badge middt-ui-badge-warning' : 'middt-ui-badge middt-ui-badge-success'">
                        {{ pgRes.usedLocalFallback ? 'Yerel Yedekleme' : 'Canlı: ' + pgRes.provider }}
                      </span>
                      @if (pgRes.totalTokens !== null && pgRes.totalTokens !== undefined) {
                        <span class="middt-ui-badge middt-ui-badge-secondary">
                          🪙 {{ pgRes.totalTokens }} token ({{ pgRes.inputTokens ?? 0 }} girdi / {{ pgRes.outputTokens ?? 0 }} çıktı)
                        </span>
                      }
                      @if (pgRes.selectedTransition) {
                        <span class="middt-ui-badge middt-ui-badge-primary">
                          🔀 Seçilen Geçiş: {{ pgRes.selectedTransition }}
                        </span>
                      }
                    </div>

                    @if (pgRes.toolCalls.length > 0) {
                      <div>
                        <strong style="font-size: .84rem">🛠️ Çağrılan Araçlar ({{ pgRes.toolCalls.length }}):</strong>
                        <div style="display: flex; flex-direction: column; gap: .35rem; margin-top: .25rem">
                          @for (tc of pgRes.toolCalls; track tc.callId) {
                            <div
                              style="background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 4px; padding: .4rem .55rem; font-size: .8rem">
                              <div><strong>#{{ tc.order + 1 }} <code>{{ tc.name }}</code></strong></div>
                              @if (tc.arguments !== undefined && tc.arguments !== null) {
                                <div style="color: #555">Argümanlar: <code>{{ formatJsonInline(tc.arguments) }}</code></div>
                              }
                              @if (tc.result !== undefined && tc.result !== null) {
                                <div style="color: #0b5ed7">Sonuç: <code>{{ formatJsonInline(tc.result) }}</code></div>
                              }
                            </div>
                          }
                        </div>
                      </div>
                    }

                    <div>
                      <strong style="font-size: .84rem">📤 Ajan Çıktısı (output):</strong>
                      <pre
                        style="background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 4px; padding: .5rem; font-size: .78rem; max-height: 220px; overflow: auto; margin: .25rem 0 0">{{ formatJsonPretty(pgRes.output) }}</pre>
                    </div>
                  </div>
                }
              </div>
            </mat-card-content>
          </mat-card>
        }
      </div>
    </div>
  `,
})
export class MiddtAgentDesignerComponent implements OnInit {
  readonly #client = inject(AGENT_API_CLIENT);
  readonly #router = inject(Router, { optional: true });
  readonly #route = inject(ActivatedRoute, { optional: true });

  readonly changed = output<void>();

  protected readonly catalog = signal<AgentCatalogModel>({
    provider: 'Local',
    defaultModelId: 'deepseek-chat',
    useLocalFallback: true,
    hasExternalApiKey: false,
    agents: [],
    tools: [],
    mcpServers: [],
  });

  protected readonly activeSection = signal<'agents' | 'tools' | 'mcp'>('agents');
  protected readonly editorMode = signal<'visual' | 'json'>('visual');
  protected readonly searchFilter = signal('');
  protected readonly busy = signal(false);
  protected readonly isNewItem = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly statusMessage = signal<string | null>(null);

  protected readonly selectedAgentName = signal<string | null>(null);
  protected readonly selectedToolName = signal<string | null>(null);
  protected readonly selectedMcpName = signal<string | null>(null);

  protected readonly draftAgent = signal<AgentDefinitionModel>({
    name: '',
    tools: [],
    mcpServers: [],
    allowedTransitions: [],
  });
  protected readonly allowedTransitionsCsv = signal('');
  protected readonly agentOutputSchemaText = signal('{\n  "type": "object",\n  "properties": {}\n}');

  protected readonly draftTool = signal<AgentToolDefinitionModel>({
    name: '',
    kind: 'expression',
    roles: [],
  });
  protected readonly toolRolesCsv = signal('');
  protected readonly toolParametersText = signal('{\n  "type": "object",\n  "properties": {}\n}');
  protected readonly toolOutputText = signal('{}');
  protected readonly toolConfigText = signal('{}');

  protected readonly draftMcp = signal<McpServerDefinitionModel>({
    name: '',
    tools: [],
    resources: [],
    prompts: [],
  });
  protected readonly mcpToolsText = signal('[]');
  protected readonly mcpResourcesText = signal('[]');
  protected readonly mcpPromptsText = signal('[]');

  protected readonly rawJsonText = signal('{}');

  protected readonly playgroundBusy = signal(false);
  protected readonly playgroundPrompt = signal(
    'Verilen işlem itirazını analiz et, araçları çağır ve uygun geçiş kararını (nextTransition) üret.',
  );
  protected readonly playgroundDataText = signal(
    '{\n  "disputeNo": "DSP-2026-0101",\n  "customerName": "Anadolu Lojistik A.Ş.",\n  "amount": 3200,\n  "category": "Mükerrer Çekim",\n  "merchantName": "Bulut Market A.Ş.",\n  "transactionRef": "TRX-9981"\n}',
  );
  protected readonly playgroundError = signal<string | null>(null);
  protected readonly playgroundResult = signal<AgentPlaygroundResultModel | null>(null);

  protected readonly filteredAgents = computed(() => {
    const q = this.searchFilter().trim().toLowerCase();
    return this.catalog().agents.filter(
      a => q === '' || a.name.toLowerCase().includes(q) || (a.description ?? '').toLowerCase().includes(q),
    );
  });

  protected readonly filteredTools = computed(() => {
    const q = this.searchFilter().trim().toLowerCase();
    return this.catalog().tools.filter(
      t => q === '' || t.name.toLowerCase().includes(q) || (t.description ?? '').toLowerCase().includes(q),
    );
  });

  protected readonly filteredMcpServers = computed(() => {
    const q = this.searchFilter().trim().toLowerCase();
    return this.catalog().mcpServers.filter(
      s => q === '' || s.name.toLowerCase().includes(q) || (s.description ?? '').toLowerCase().includes(q),
    );
  });

  protected readonly availableToolNames = computed(() => {
    const set = new Set<string>();
    for (const t of this.catalog().tools) {
      set.add(t.name);
    }
    for (const s of this.catalog().mcpServers) {
      for (const mt of s.tools ?? []) {
        set.add(mt.name);
      }
    }
    for (const existing of this.draftAgent().tools ?? []) {
      set.add(existing);
    }
    return [...set].sort();
  });

  protected readonly currentItemTitle = computed(() => {
    const sec = this.activeSection();
    if (sec === 'agents') {
      return this.draftAgent().name || 'Yeni Ajan';
    }
    if (sec === 'tools') {
      return this.draftTool().name || 'Yeni Araç';
    }
    return this.draftMcp().name || 'Yeni MCP Sunucusu';
  });

  ngOnInit(): void {
    void this.initAsync();
  }

  private async initAsync(): Promise<void> {
    await this.reloadCatalog();
    const params = this.#route?.snapshot?.queryParamMap;
    const ag = params?.get('agent');
    const tl = params?.get('tool');
    const mcp = params?.get('mcp');
    if (ag) {
      this.activeSection.set('agents');
      this.selectAgent(ag);
    } else if (tl) {
      this.activeSection.set('tools');
      this.selectTool(tl);
    } else if (mcp) {
      this.activeSection.set('mcp');
      this.selectMcpServer(mcp);
    }
  }

  protected async reloadCatalog(): Promise<void> {
    const resp = await this.#client.getCatalog();
    if (isSuccess(resp) && resp.data) {
      this.catalog.set(resp.data);
      const curAgent = this.selectedAgentName();
      if (!curAgent && resp.data.agents.length > 0) {
        this.selectAgent(resp.data.agents[0]!.name);
      } else if (curAgent && resp.data.agents.some(a => a.name.toLowerCase() === curAgent.toLowerCase())) {
        this.selectAgent(curAgent);
      } else if (resp.data.agents.length === 0) {
        this.createNewAgent();
      }

      if (!this.selectedToolName() && resp.data.tools.length > 0) {
        this.loadToolIntoDraft(resp.data.tools[0]!);
      }
      if (!this.selectedMcpName() && resp.data.mcpServers.length > 0) {
        this.loadMcpIntoDraft(resp.data.mcpServers[0]!);
      }
    }
  }

  protected switchSection(section: 'agents' | 'tools' | 'mcp'): void {
    this.errors.set([]);
    this.statusMessage.set(null);
    this.activeSection.set(section);
    if (section === 'agents' && !this.selectedAgentName() && this.catalog().agents.length > 0) {
      this.selectAgent(this.catalog().agents[0]!.name);
    } else if (section === 'tools' && !this.selectedToolName() && this.catalog().tools.length > 0) {
      this.selectTool(this.catalog().tools[0]!.name);
    } else if (section === 'mcp' && !this.selectedMcpName() && this.catalog().mcpServers.length > 0) {
      this.selectMcpServer(this.catalog().mcpServers[0]!.name);
    }
    this.syncDraftToRawJson();
  }

  protected switchEditorMode(mode: 'visual' | 'json'): void {
    if (this.editorMode() === 'json' && mode === 'visual') {
      this.applyRawJsonToDraft();
    } else if (mode === 'json') {
      this.syncDraftToRawJson();
    }
    this.editorMode.set(mode);
  }

  protected openWorkflowDesigner(): void {
    void this.#router?.navigate(['/workflow-admin']);
  }

  protected selectAgent(name: string): void {
    const found = this.catalog().agents.find(a => a.name.toLowerCase() === name.toLowerCase());
    if (!found) {
      return;
    }
    this.isNewItem.set(false);
    this.selectedAgentName.set(found.name);
    const cloned = structuredClone(found);
    cloned.tools ??= [];
    cloned.mcpServers ??= [];
    cloned.allowedTransitions ??= [];
    this.draftAgent.set(cloned);
    this.allowedTransitionsCsv.set(cloned.allowedTransitions.join(', '));
    this.agentOutputSchemaText.set(
      cloned.outputSchema ? JSON.stringify(cloned.outputSchema, null, 2) : '{\n  "type": "object",\n  "properties": {}\n}',
    );
    this.syncDraftToRawJson();
  }

  protected selectTool(name: string): void {
    const found = this.catalog().tools.find(t => t.name.toLowerCase() === name.toLowerCase());
    if (found) {
      this.isNewItem.set(false);
      this.loadToolIntoDraft(found);
      this.syncDraftToRawJson();
    }
  }

  private loadToolIntoDraft(tool: AgentToolDefinitionModel): void {
    this.selectedToolName.set(tool.name);
    const cloned = structuredClone(tool);
    if (cloned.task && (!cloned.kind || cloned.kind === 'expression')) {
      cloned.kind = 'task';
    }
    cloned.roles ??= [];
    this.draftTool.set(cloned);
    this.toolRolesCsv.set(cloned.roles.join(', '));
    this.toolParametersText.set(
      cloned.parameters ? JSON.stringify(cloned.parameters, null, 2) : '{\n  "type": "object",\n  "properties": {}\n}',
    );
    this.toolOutputText.set(cloned.output !== undefined && cloned.output !== null ? JSON.stringify(cloned.output, null, 2) : '{}');
    this.toolConfigText.set(cloned.config ? JSON.stringify(cloned.config, null, 2) : '{}');
  }

  protected selectMcpServer(name: string): void {
    const found = this.catalog().mcpServers.find(s => s.name.toLowerCase() === name.toLowerCase());
    if (found) {
      this.isNewItem.set(false);
      this.loadMcpIntoDraft(found);
      this.syncDraftToRawJson();
    }
  }

  private loadMcpIntoDraft(srv: McpServerDefinitionModel): void {
    this.selectedMcpName.set(srv.name);
    const cloned = structuredClone(srv);
    cloned.tools ??= [];
    cloned.resources ??= [];
    cloned.prompts ??= [];
    this.draftMcp.set(cloned);
    this.mcpToolsText.set(JSON.stringify(cloned.tools, null, 2));
    this.mcpResourcesText.set(JSON.stringify(cloned.resources, null, 2));
    this.mcpPromptsText.set(JSON.stringify(cloned.prompts, null, 2));
  }

  protected createNewItem(): void {
    this.errors.set([]);
    this.statusMessage.set(null);
    this.isNewItem.set(true);
    const sec = this.activeSection();
    if (sec === 'agents') {
      this.createNewAgent();
    } else if (sec === 'tools') {
      this.selectedToolName.set(null);
      this.draftTool.set({
        name: 'yeni_arac',
        kind: 'expression',
        description: 'Yeni deklaratif AI aracı',
        roles: [],
      });
      this.toolRolesCsv.set('');
      this.toolParametersText.set('{\n  "type": "object",\n  "properties": {\n    "amount": { "type": "number" }\n  }\n}');
      this.toolOutputText.set('{\n  "approved": "{{ args.amount <= 10000 }}"\n}');
      this.toolConfigText.set('{}');
    } else {
      this.selectedMcpName.set(null);
      this.draftMcp.set({
        name: 'yeni-mcp',
        description: 'Yeni Model Context Protocol Sunucusu',
        tools: [],
        resources: [],
        prompts: [],
      });
      this.mcpToolsText.set('[]');
      this.mcpResourcesText.set('[]');
      this.mcpPromptsText.set('[]');
    }
    this.syncDraftToRawJson();
  }

  private createNewAgent(): void {
    this.selectedAgentName.set(null);
    this.draftAgent.set({
      name: 'yeni-ai-ajan',
      description: 'İş akışı karar ve analiz ajanı',
      instructions: 'Sen uzman bir iş akışı ajanısın. Verilen veriyi analiz ederek JSON şemasına uygun yanıt üret.',
      maxToolCalls: 8,
      maxTotalTokens: 12000,
      tools: [],
      mcpServers: [],
      allowedTransitions: [],
    });
    this.allowedTransitionsCsv.set('');
    this.agentOutputSchemaText.set(
      '{\n  "type": "object",\n  "required": ["decision", "summary"],\n  "properties": {\n    "decision": { "type": "string", "default": "Onaylandı" },\n    "summary": { "type": "string", "default": "Değerlendirme tamamlandı." }\n  }\n}',
    );
  }

  protected updateAgentField<K extends keyof AgentDefinitionModel>(field: K, value: AgentDefinitionModel[K]): void {
    this.draftAgent.update(cur => ({ ...cur, [field]: value }));
  }

  protected updateAgentNumber(
    field: 'temperature' | 'maxOutputTokens' | 'maxToolCalls' | 'maxTotalTokens',
    raw: string,
  ): void {
    const num = raw.trim() === '' ? null : Number(raw);
    this.draftAgent.update(cur => ({ ...cur, [field]: Number.isFinite(num) ? num : null }));
  }

  protected hasAgentTool(toolName: string): boolean {
    return (this.draftAgent().tools ?? []).some(t => t.toLowerCase() === toolName.toLowerCase());
  }

  protected toggleAgentTool(toolName: string, enabled: boolean): void {
    this.draftAgent.update(cur => {
      const existing = [...(cur.tools ?? [])];
      if (enabled) {
        if (!existing.some(t => t.toLowerCase() === toolName.toLowerCase())) {
          existing.push(toolName);
        }
      } else {
        return { ...cur, tools: existing.filter(t => t.toLowerCase() !== toolName.toLowerCase()) };
      }
      return { ...cur, tools: existing };
    });
  }

  protected hasAgentMcpServer(serverName: string): boolean {
    return (this.draftAgent().mcpServers ?? []).some(s => s.toLowerCase() === serverName.toLowerCase());
  }

  protected toggleAgentMcpServer(serverName: string, enabled: boolean): void {
    this.draftAgent.update(cur => {
      const existing = [...(cur.mcpServers ?? [])];
      if (enabled) {
        if (!existing.some(s => s.toLowerCase() === serverName.toLowerCase())) {
          existing.push(serverName);
        }
      } else {
        return { ...cur, mcpServers: existing.filter(s => s.toLowerCase() !== serverName.toLowerCase()) };
      }
      return { ...cur, mcpServers: existing };
    });
  }

  protected onAllowedTransitionsInput(raw: string): void {
    this.allowedTransitionsCsv.set(raw);
    const list = raw
      .split(',')
      .map(s => s.trim())
      .filter(s => s.length > 0);
    this.draftAgent.update(cur => ({ ...cur, allowedTransitions: list }));
  }

  protected updateToolField<K extends keyof AgentToolDefinitionModel>(field: K, value: AgentToolDefinitionModel[K]): void {
    this.draftTool.update(cur => ({ ...cur, [field]: value }));
  }

  protected onToolRolesInput(raw: string): void {
    this.toolRolesCsv.set(raw);
    const list = raw
      .split(',')
      .map(s => s.trim())
      .filter(s => s.length > 0);
    this.draftTool.update(cur => ({ ...cur, roles: list }));
  }

  protected updateMcpField<K extends keyof McpServerDefinitionModel>(field: K, value: McpServerDefinitionModel[K]): void {
    this.draftMcp.update(cur => ({ ...cur, [field]: value }));
  }

  private trySyncVisualFieldsToModels(): string | null {
    try {
      const sec = this.activeSection();
      if (sec === 'agents') {
        const schemaTxt = this.agentOutputSchemaText().trim();
        const schema = schemaTxt ? (JSON.parse(schemaTxt) as WorkflowData) : null;
        this.draftAgent.update(cur => ({ ...cur, outputSchema: schema }));
      } else if (sec === 'tools') {
        const paramsTxt = this.toolParametersText().trim();
        const params = paramsTxt ? (JSON.parse(paramsTxt) as WorkflowData) : null;
        const kind = this.draftTool().kind || 'expression';
        const outputVal = kind === 'expression' && this.toolOutputText().trim() ? JSON.parse(this.toolOutputText()) : null;
        const cfgVal = kind === 'task' && this.toolConfigText().trim() ? (JSON.parse(this.toolConfigText()) as WorkflowData) : null;
        this.draftTool.update(cur => ({
          ...cur,
          parameters: params,
          output: kind === 'expression' ? outputVal : cur.output,
          config: kind === 'task' ? cfgVal : cur.config,
        }));
      } else {
        const tools = JSON.parse(this.mcpToolsText()) as AgentToolDefinitionModel[];
        const resources = JSON.parse(this.mcpResourcesText()) as McpResourceDefinitionModel[];
        const prompts = JSON.parse(this.mcpPromptsText()) as McpPromptDefinitionModel[];
        this.draftMcp.update(cur => ({ ...cur, tools, resources, prompts }));
      }
      return null;
    } catch (e) {
      return `JSON alanı ayrıştırılamadı: ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  private syncDraftToRawJson(): void {
    this.trySyncVisualFieldsToModels();
    const sec = this.activeSection();
    if (sec === 'agents') {
      this.rawJsonText.set(JSON.stringify(this.draftAgent(), null, 2));
    } else if (sec === 'tools') {
      this.rawJsonText.set(JSON.stringify(this.draftTool(), null, 2));
    } else {
      this.rawJsonText.set(JSON.stringify(this.draftMcp(), null, 2));
    }
  }

  protected applyRawJsonToDraft(): void {
    this.errors.set([]);
    try {
      const sec = this.activeSection();
      if (sec === 'agents') {
        const parsed = JSON.parse(this.rawJsonText()) as AgentDefinitionModel;
        this.draftAgent.set(parsed);
        this.allowedTransitionsCsv.set((parsed.allowedTransitions ?? []).join(', '));
        this.agentOutputSchemaText.set(parsed.outputSchema ? JSON.stringify(parsed.outputSchema, null, 2) : '{}');
      } else if (sec === 'tools') {
        const parsed = JSON.parse(this.rawJsonText()) as AgentToolDefinitionModel;
        this.loadToolIntoDraft(parsed);
      } else {
        const parsed = JSON.parse(this.rawJsonText()) as McpServerDefinitionModel;
        this.loadMcpIntoDraft(parsed);
      }
    } catch (e) {
      this.errors.set([`Geçersiz JSON: ${e instanceof Error ? e.message : String(e)}`]);
    }
  }

  protected async validateCurrent(): Promise<void> {
    this.errors.set([]);
    this.statusMessage.set(null);
    if (this.editorMode() === 'json') {
      this.applyRawJsonToDraft();
      if (this.errors().length > 0) {
        return;
      }
    } else {
      const err = this.trySyncVisualFieldsToModels();
      if (err) {
        this.errors.set([err]);
        return;
      }
    }

    const sec = this.activeSection();
    const payload: WorkflowData =
      sec === 'agents'
        ? { agents: [this.draftAgent()] }
        : sec === 'tools'
          ? { tools: [this.draftTool()] }
          : { mcpServers: [this.draftMcp()] };

    const resp = await this.#client.validate(payload);
    if (!isSuccess(resp)) {
      this.errors.set(resp.messageList.length > 0 ? resp.messageList : ['Doğrulama başarısız oldu.']);
      return;
    }
    if (resp.data && !resp.data.valid) {
      this.errors.set(resp.data.errors);
      return;
    }
    this.statusMessage.set('Tanım doğrulandı; hata bulunmadı.');
  }

  protected async saveCurrent(): Promise<void> {
    this.errors.set([]);
    this.statusMessage.set(null);
    if (this.editorMode() === 'json') {
      this.applyRawJsonToDraft();
      if (this.errors().length > 0) {
        return;
      }
    } else {
      const err = this.trySyncVisualFieldsToModels();
      if (err) {
        this.errors.set([err]);
        return;
      }
    }

    this.busy.set(true);
    try {
      const sec = this.activeSection();
      if (sec === 'agents') {
        const resp = await this.#client.saveAgent(this.draftAgent());
        if (!isSuccess(resp)) {
          this.errors.set(resp.messageList.length > 0 ? resp.messageList : ['Ajan kaydedilemedi.']);
          return;
        }
        this.selectedAgentName.set(this.draftAgent().name);
        this.statusMessage.set(`'${this.draftAgent().name}' AI ajanı kaydedildi.`);
      } else if (sec === 'tools') {
        const resp = await this.#client.saveTool(this.draftTool());
        if (!isSuccess(resp)) {
          this.errors.set(resp.messageList.length > 0 ? resp.messageList : ['Araç kaydedilemedi.']);
          return;
        }
        this.selectedToolName.set(this.draftTool().name);
        this.statusMessage.set(`'${this.draftTool().name}' aracı kaydedildi.`);
      } else {
        const resp = await this.#client.saveMcpServer(this.draftMcp());
        if (!isSuccess(resp)) {
          this.errors.set(resp.messageList.length > 0 ? resp.messageList : ['MCP sunucusu kaydedilemedi.']);
          return;
        }
        this.selectedMcpName.set(this.draftMcp().name);
        this.statusMessage.set(`'${this.draftMcp().name}' MCP sunucusu kaydedildi.`);
      }

      this.isNewItem.set(false);
      await this.reloadCatalog();
      this.changed.emit();
    } finally {
      this.busy.set(false);
    }
  }

  protected async deleteCurrent(): Promise<void> {
    this.errors.set([]);
    this.statusMessage.set(null);
    this.busy.set(true);
    try {
      const sec = this.activeSection();
      if (sec === 'agents' && this.selectedAgentName()) {
        const name = this.selectedAgentName()!;
        const resp = await this.#client.deleteAgent(name);
        if (!isSuccess(resp)) {
          this.errors.set(resp.messageList);
          return;
        }
        this.selectedAgentName.set(null);
        this.statusMessage.set(`'${name}' ajanı silindi.`);
      } else if (sec === 'tools' && this.selectedToolName()) {
        const name = this.selectedToolName()!;
        const resp = await this.#client.deleteTool(name);
        if (!isSuccess(resp)) {
          this.errors.set(resp.messageList);
          return;
        }
        this.selectedToolName.set(null);
        this.statusMessage.set(`'${name}' aracı silindi.`);
      } else if (sec === 'mcp' && this.selectedMcpName()) {
        const name = this.selectedMcpName()!;
        const resp = await this.#client.deleteMcpServer(name);
        if (!isSuccess(resp)) {
          this.errors.set(resp.messageList);
          return;
        }
        this.selectedMcpName.set(null);
        this.statusMessage.set(`'${name}' MCP sunucusu silindi.`);
      }
      await this.reloadCatalog();
      this.changed.emit();
    } finally {
      this.busy.set(false);
    }
  }

  protected applyPlaygroundPreset(preset: string): void {
    switch (preset) {
      case 'dispute-low':
        this.playgroundPrompt.set(
          'Düşük tutarlı mükerrer çekim itirazını evaluate_dispute_policy ve banking-mcp araçlarıyla değerlendir, uygun iş akışı geçişini (nextTransition) seç.',
        );
        this.playgroundDataText.set(
          '{\n  "disputeNo": "DSP-2026-0101",\n  "customerName": "Anadolu Lojistik A.Ş.",\n  "amount": 3200,\n  "category": "Mükerrer Çekim",\n  "merchantName": "Bulut Market A.Ş.",\n  "transactionRef": "TRX-9981"\n}',
        );
        break;
      case 'dispute-fraud':
        this.playgroundPrompt.set(
          'Yüksek tutarlı yetkisiz işlem bildirimini incele ve güvenlik / fraud eskalasyon kararını üret.',
        );
        this.playgroundDataText.set(
          '{\n  "disputeNo": "DSP-2026-0102",\n  "customerName": "Kuzey Denizcilik A.Ş.",\n  "amount": 48000,\n  "category": "Yetkisiz İşlem",\n  "merchantName": "Global Elektronik",\n  "transactionRef": "TRX-9982"\n}',
        );
        break;
      case 'dispute-specialist':
        this.playgroundPrompt.set(
          'Uzman incelemesindeki dosya için calculate_reimbursement aracını kullanarak iade ve iyi niyet kredisini hesapla.',
        );
        this.playgroundDataText.set(
          '{\n  "disputeNo": "DSP-2026-0103",\n  "customerName": "Marmara Tekstil Ltd.",\n  "amount": 14500,\n  "category": "Hizmet Kusuru",\n  "merchantName": "Lojistik Ekspres"\n}',
        );
        break;
      case 'credit-risk':
        this.playgroundPrompt.set(
          'Müşterinin kredi tahsis ve teminat verilerini analiz ederek kredi notunu (creditGrade) ve yönlendirme kararını üret.',
        );
        this.playgroundDataText.set(
          '{\n  "customerName": "Ege Otomotiv Sanayi A.Ş.",\n  "segment": "Kurumsal",\n  "requestedAmount": 750000,\n  "monthlyIncome": 420000,\n  "collateralAmount": 900000,\n  "sector": "Üretim"\n}',
        );
        break;
      case 'invoice-audit':
        this.playgroundPrompt.set(
          'Fatura tahsilat ve kısmi ödeme geçmişini denetleyerek mutabakat risk raporunu oluştur.',
        );
        this.playgroundDataText.set(
          '{\n  "invoiceNo": "FTR-2026-0001",\n  "customerName": "Anadolu Lojistik A.Ş.",\n  "total": 12500,\n  "paidAmount": 5000,\n  "remainingAmount": 7500\n}',
        );
        break;
    }
  }

  protected async runPlayground(): Promise<void> {
    this.playgroundError.set(null);
    this.playgroundResult.set(null);
    const syncErr = this.trySyncVisualFieldsToModels();
    if (syncErr) {
      this.playgroundError.set(syncErr);
      return;
    }

    let dataObj: WorkflowData | null = null;
    if (this.playgroundDataText().trim() !== '') {
      try {
        dataObj = JSON.parse(this.playgroundDataText()) as WorkflowData;
      } catch (e) {
        this.playgroundError.set(`Örnek veri JSON formatı hatalı: ${e instanceof Error ? e.message : String(e)}`);
        return;
      }
    }

    this.playgroundBusy.set(true);
    try {
      const def = this.draftAgent();
      const allowed =
        def.allowedTransitions && def.allowedTransitions.length > 0
          ? def.allowedTransitions
          : ['route-auto-refund', 'route-specialist-review', 'route-fraud-investigation'];

      const resp = await this.#client.testPlayground({
        agent: def.name,
        definition: def,
        prompt: this.playgroundPrompt(),
        data: dataObj,
        allowedTransitions: allowed,
      });

      if (!isSuccess(resp) || !resp.data) {
        this.playgroundError.set(resp.messageList.length > 0 ? resp.messageList.join(' ') : 'Playground çalıştırılamadı.');
        return;
      }

      this.playgroundResult.set(resp.data);
    } finally {
      this.playgroundBusy.set(false);
    }
  }

  protected formatJsonInline(val: unknown): string {
    return JSON.stringify(val);
  }

  protected formatJsonPretty(val: unknown): string {
    return val === undefined || val === null ? 'null' : JSON.stringify(val, null, 2);
  }
}
