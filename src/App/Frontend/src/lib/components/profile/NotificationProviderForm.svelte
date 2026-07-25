<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { Select } from '$lib/components/ui';
  import {
    ArrowLeft,
    Bell,
    CircleAlert,
    CircleCheck,
    FlaskConical,
    Plus,
    Send
  } from '@lucide/svelte';
  import {
    NOTIFICATION_PROVIDER_KEY_PATTERN,
    NOTIFICATION_PROVIDER_KINDS,
    sendTestNotification,
    upsertNotificationProvider,
    upsertNotificationProviderSecrets,
    type NotificationProvider
  } from '$lib/api/notifications';

  interface Props {
    mode: 'create' | 'update';
    initial?: NotificationProvider | null;
  }

  let { mode, initial = null }: Props = $props();

  interface SelectItem {
    value: string;
    name: string;
  }

  type FieldType = 'text' | 'password' | 'number' | 'checkbox' | 'textarea';

  interface ConfigField {
    key: string;
    label: string;
    type?: FieldType;
    placeholder?: string;
    secret?: boolean;
  }

  interface ServiceOption {
    value: string;
    name: string;
    configKey: string;
    fields: ConfigField[];
  }

  interface ProviderFormDefinition {
    name: string;
    fields?: ConfigField[];
    serviceLabel?: string;
    serviceConfigKey?: string;
    services?: ServiceOption[];
  }

  const checkboxClass = 'text-sm text-base-content/80';

  const isUpdate = untrack(() => mode === 'update');

  let providerKey = $state(untrack(() => initial?.providerKey ?? ''));
  let providerKind = $state(untrack(() => initial?.providerKind ?? 'email'));
  let providerEnabled = $state(untrack(() => initial?.enabled ?? true));
  let displayName = $state(untrack(() => initial?.displayName ?? ''));
  let defaultTo = $state(untrack(() => initial?.defaultTo ?? ''));
  let serviceProvider = $state('');
  let configValues = $state<Record<string, string | boolean>>({});
  let secretValues = $state<Record<string, string>>({});
  let preservedSecretRefs = $state<Record<string, string>>({});
  let savingProvider = $state(false);
  let submitError = $state<string | null>(null);

  let testSubject = $state('FrostStream notification test');
  let testBody = $state('This is a test notification from FrostStream.');
  let testingProvider = $state(false);
  let testError = $state<string | null>(null);
  let testStatus = $state<string | null>(null);

  const activeDefinition = $derived(providerDefinition(providerKind));
  const activeService = $derived(activeDefinition.services?.find((service) => service.value === serviceProvider) ?? null);
  const activeFields = $derived(activeService?.fields ?? activeDefinition.fields ?? []);

  const providerDefinitions: Record<string, ProviderFormDefinition> = {
    email: {
      name: 'Email',
      serviceLabel: 'Email service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'smtp',
          name: 'SMTP',
          configKey: 'smtp',
          fields: [
            { key: 'host', label: 'SMTP host', placeholder: 'smtp.example.com' },
            { key: 'port', label: 'Port', type: 'number', placeholder: '587' },
            { key: 'useSsl', label: 'Use SSL/TLS', type: 'checkbox' },
            { key: 'username', label: 'Username' },
            { key: 'password', label: 'Password', type: 'password', secret: true },
            { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        },
        { value: 'sendGrid', name: 'SendGrid', configKey: 'sendGrid', fields: emailApiFields() },
        { value: 'postmark', name: 'Postmark', configKey: 'postmark', fields: emailApiFields() },
        { value: 'resend', name: 'Resend', configKey: 'resend', fields: emailApiFields() },
        {
          value: 'mailgun',
          name: 'Mailgun',
          configKey: 'mailgun',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'domain', label: 'Domain', placeholder: 'mg.example.com' },
            { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        },
        {
          value: 'awsSes',
          name: 'AWS SES',
          configKey: 'awsSes',
          fields: [
            { key: 'region', label: 'Region', placeholder: 'us-east-1' },
            { key: 'accessKey', label: 'Access key', type: 'password', secret: true },
            { key: 'secretKey', label: 'Secret key', type: 'password', secret: true },
            { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        },
        {
          value: 'azureCommEmail',
          name: 'Azure Communication Email',
          configKey: 'azureCommEmail',
          fields: [
            { key: 'connectionString', label: 'Connection string', type: 'password', secret: true },
            { key: 'fromEmail', label: 'From email', placeholder: 'DoNotReply@example.azurecomm.net' },
            { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
          ]
        }
      ]
    },
    sms: {
      name: 'SMS',
      serviceLabel: 'SMS service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'twilio',
          name: 'Twilio',
          configKey: 'twilio',
          fields: [
            { key: 'accountSid', label: 'Account SID' },
            { key: 'authToken', label: 'Auth token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'vonage',
          name: 'Vonage',
          configKey: 'vonage',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'apiSecret', label: 'API secret', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'sinch',
          name: 'Sinch',
          configKey: 'sinch',
          fields: [
            { key: 'servicePlanId', label: 'Service plan ID' },
            { key: 'apiToken', label: 'API token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'messageBird',
          name: 'MessageBird',
          configKey: 'messageBird',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'originator', label: 'Originator', placeholder: 'FrostStream' }
          ]
        },
        {
          value: 'plivo',
          name: 'Plivo',
          configKey: 'plivo',
          fields: [
            { key: 'authId', label: 'Auth ID' },
            { key: 'authToken', label: 'Auth token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'awsSns',
          name: 'AWS SNS',
          configKey: 'awsSns',
          fields: [
            { key: 'region', label: 'Region', placeholder: 'us-east-1' },
            { key: 'accessKey', label: 'Access key', type: 'password', secret: true },
            { key: 'secretKey', label: 'Secret key', type: 'password', secret: true },
            { key: 'senderId', label: 'Sender ID', placeholder: 'FROST' },
            { key: 'smsType', label: 'SMS type', placeholder: 'Transactional' }
          ]
        },
        {
          value: 'azureCommSms',
          name: 'Azure Communication SMS',
          configKey: 'azureCommSms',
          fields: [
            { key: 'connectionString', label: 'Connection string', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'msg91',
          name: 'MSG91',
          configKey: 'msg91',
          fields: [
            { key: 'authKey', label: 'Auth key', type: 'password', secret: true },
            { key: 'senderId', label: 'Sender ID' },
            { key: 'route', label: 'Route' }
          ]
        }
      ]
    },
    push: {
      name: 'Push',
      serviceLabel: 'Push service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'oneSignal',
          name: 'OneSignal',
          configKey: 'oneSignal',
          fields: [
            { key: 'appId', label: 'App ID' },
            { key: 'apiKey', label: 'API key', type: 'password', secret: true }
          ]
        },
        {
          value: 'fcm',
          name: 'Firebase Cloud Messaging',
          configKey: 'fcm',
          fields: [
            { key: 'projectId', label: 'Project ID' },
            { key: 'serviceAccountJson', label: 'Service account JSON', type: 'textarea', secret: true }
          ]
        },
        {
          value: 'expo',
          name: 'Expo',
          configKey: 'expo',
          fields: [{ key: 'accessToken', label: 'Access token', type: 'password', secret: true }]
        },
        {
          value: 'apns',
          name: 'Apple Push Notification service',
          configKey: 'apns',
          fields: [
            { key: 'bundleId', label: 'Bundle ID' },
            { key: 'teamId', label: 'Team ID' },
            { key: 'keyId', label: 'Key ID' },
            { key: 'privateKey', label: 'Private key', type: 'textarea', secret: true }
          ]
        }
      ]
    },
    whatsapp: {
      name: 'WhatsApp',
      serviceLabel: 'WhatsApp service',
      serviceConfigKey: 'provider',
      services: [
        {
          value: 'twilio',
          name: 'Twilio',
          configKey: 'twilio',
          fields: [
            { key: 'accountSid', label: 'Account SID' },
            { key: 'authToken', label: 'Auth token', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: 'whatsapp:+15551234567' }
          ]
        },
        {
          value: 'metaCloud',
          name: 'Meta Cloud',
          configKey: 'metaCloud',
          fields: [
            { key: 'accessToken', label: 'Access token', type: 'password', secret: true },
            { key: 'phoneNumberId', label: 'Phone number ID' }
          ]
        },
        {
          value: 'vonage',
          name: 'Vonage',
          configKey: 'vonage',
          fields: [
            { key: 'apiKey', label: 'API key', type: 'password', secret: true },
            { key: 'apiSecret', label: 'API secret', type: 'password', secret: true },
            { key: 'fromNumber', label: 'From number', placeholder: '+15551234567' }
          ]
        },
        {
          value: 'msg91',
          name: 'MSG91',
          configKey: 'msg91',
          fields: [
            { key: 'authKey', label: 'Auth key', type: 'password', secret: true },
            { key: 'integratedNumber', label: 'Integrated number' },
            { key: 'namespace', label: 'Namespace' }
          ]
        }
      ]
    },
    slack: {
      name: 'Slack',
      fields: [
        { key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true },
        { key: 'botToken', label: 'Bot token', type: 'password', secret: true }
      ]
    },
    discord: { name: 'Discord', fields: [{ key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true }] },
    teams: { name: 'Microsoft Teams', fields: [{ key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true }] },
    telegram: {
      name: 'Telegram',
      fields: [
        { key: 'botToken', label: 'Bot token', type: 'password', secret: true },
        { key: 'chatId', label: 'Chat ID' },
        { key: 'parseMode', label: 'Parse mode', placeholder: 'Markdown' }
      ]
    },
    facebook: { name: 'Facebook', fields: [{ key: 'pageAccessToken', label: 'Page access token', type: 'password', secret: true }] },
    line: { name: 'Line', fields: [{ key: 'channelAccessToken', label: 'Channel access token', type: 'password', secret: true }] },
    viber: {
      name: 'Viber',
      fields: [
        { key: 'botAuthToken', label: 'Bot auth token', type: 'password', secret: true },
        { key: 'senderName', label: 'Sender name', placeholder: 'FrostStream' },
        { key: 'senderAvatarUrl', label: 'Sender avatar URL' }
      ]
    },
    mattermost: {
      name: 'Mattermost',
      fields: [
        { key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true },
        { key: 'channel', label: 'Channel', placeholder: 'town-square' },
        { key: 'username', label: 'Username', placeholder: 'FrostStream' }
      ]
    },
    rocketchat: {
      name: 'Rocket.Chat',
      fields: [
        { key: 'webhookUrl', label: 'Webhook URL', type: 'password', secret: true },
        { key: 'channel', label: 'Channel', placeholder: '#general' },
        { key: 'username', label: 'Username', placeholder: 'FrostStream' }
      ]
    }
  };

  const providerKindItems: SelectItem[] = NOTIFICATION_PROVIDER_KINDS.map((kind) => ({
    value: kind,
    name: providerDefinitions[kind]?.name ?? titleCase(kind)
  }));

  untrack(() => {
    if (initial) {
      hydrateConfig(initial.providerKind, initial.notifyConfig);
    } else {
      resetConfigForKind(providerKind);
    }
  });

  async function saveProvider(event: SubmitEvent) {
    event.preventDefault();
    submitError = null;

    const key = providerKey.trim();
    if (!NOTIFICATION_PROVIDER_KEY_PATTERN.test(key)) {
      submitError = 'Provider key must match ^[a-z0-9-]{2,100}$.';
      return;
    }

    let notifyConfig: Record<string, unknown>;
    let secrets: Record<string, string>;
    try {
      ({ notifyConfig, secrets } = buildNotifyConfig(key));
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'Notification provider settings are invalid.';
      return;
    }

    savingProvider = true;
    try {
      await upsertNotificationProvider(key, {
        providerKey: key,
        providerKind,
        enabled: providerEnabled,
        displayName: displayName.trim() || null,
        defaultTo: defaultTo.trim() || null,
        notifyConfig
      });
      if (Object.keys(secrets).length > 0) {
        await upsertNotificationProviderSecrets(key, { secrets });
      }
      await goto('/profile/notifications');
    } catch (err) {
      submitError = err instanceof Error ? err.message : 'Could not save the notification provider.';
    } finally {
      savingProvider = false;
    }
  }

  async function sendTest() {
    testError = null;
    testStatus = null;

    if (!initial) {
      testError = 'Save the provider before sending a test notification.';
      return;
    }

    testingProvider = true;
    try {
      await sendTestNotification({
        providerKey: initial.providerKey,
        subject: testSubject.trim() || null,
        body: testBody.trim() || null
      });
      testStatus = 'Test notification sent.';
    } catch (err) {
      testError = err instanceof Error ? err.message : 'Could not send the test notification.';
    } finally {
      testingProvider = false;
    }
  }

  function onProviderKindChange() {
    resetConfigForKind(providerKind);
  }

  function onServiceProviderChange() {
    configValues = {};
    secretValues = {};
    preservedSecretRefs = {};
  }

  function updateConfigValue(fieldKey: string, value: string) {
    configValues = { ...configValues, [fieldKey]: value };
  }

  function updateSecretValue(fieldKey: string, value: string) {
    secretValues = { ...secretValues, [fieldKey]: value };
  }

  function updateCheckboxValue(fieldKey: string, value: boolean) {
    configValues = { ...configValues, [fieldKey]: value };
  }

  function inputValue(event: Event): string {
    return (event.currentTarget as HTMLInputElement | HTMLTextAreaElement | null)?.value ?? '';
  }

  function resetConfigForKind(kind: string) {
    const definition = providerDefinition(kind);
    serviceProvider = definition.services?.[0]?.value ?? '';
    configValues = defaultConfigValues(definition.fields ?? definition.services?.[0]?.fields ?? []);
    secretValues = {};
    preservedSecretRefs = {};
  }

  function hydrateConfig(kind: string, config: Record<string, unknown> | null | undefined) {
    const definition = providerDefinition(kind);
    const source = isRecord(config) ? config : {};
    serviceProvider = readString(source[definition.serviceConfigKey ?? 'provider'])
      || definition.services?.find((service) => isRecord(source[service.configKey]))?.value
      || definition.services?.[0]?.value
      || '';

    const service = definition.services?.find((item) => item.value === serviceProvider) ?? null;
    const fields = service?.fields ?? definition.fields ?? [];
    const fieldSource = service ? readRecord(source[service.configKey]) : source;
    const nextValues = defaultConfigValues(fields);
    const nextRefs: Record<string, string> = {};

    for (const field of fields) {
      const raw = fieldSource[field.key];
      if (field.secret && typeof raw === 'string' && raw.startsWith('secret://')) {
        nextRefs[field.key] = raw;
        nextValues[field.key] = '';
      } else if (field.type === 'checkbox') {
        nextValues[field.key] = raw === true;
      } else if (field.type === 'number') {
        nextValues[field.key] = raw === undefined || raw === null ? '' : String(raw);
      } else {
        nextValues[field.key] = raw === undefined || raw === null ? '' : String(raw);
      }
    }

    configValues = nextValues;
    secretValues = {};
    preservedSecretRefs = nextRefs;
  }

  function buildNotifyConfig(key: string): { notifyConfig: Record<string, unknown>; secrets: Record<string, string> } {
    const definition = providerDefinition(providerKind);
    const service = definition.services?.find((item) => item.value === serviceProvider) ?? null;
    const fields = service?.fields ?? definition.fields ?? [];
    const fieldConfig = buildFieldConfig(key, fields);
    const notifyConfig: Record<string, unknown> = {};

    if (service) {
      notifyConfig[definition.serviceConfigKey ?? 'provider'] = service.value;
      notifyConfig[service.configKey] = fieldConfig.config;
    } else {
      Object.assign(notifyConfig, fieldConfig.config);
    }

    return { notifyConfig, secrets: fieldConfig.secrets };
  }

  function buildFieldConfig(
    key: string,
    fields: ConfigField[]
  ): { config: Record<string, unknown>; secrets: Record<string, string> } {
    const config: Record<string, unknown> = {};
    const secrets: Record<string, string> = {};

    for (const field of fields) {
      if (field.type === 'checkbox') {
        config[field.key] = configValues[field.key] === true;
        continue;
      }

      if (field.secret) {
        const secretValueForField = (secretValues[field.key] ?? '').trim();
        if (secretValueForField) {
          secrets[secretNameForField(field.key)] = secretValueForField;
          config[field.key] = `secret://${key}/${secretNameForField(field.key)}`;
        } else if (preservedSecretRefs[field.key]) {
          config[field.key] = preservedSecretRefs[field.key];
        }
        continue;
      }

      const raw = configValues[field.key];
      const value = typeof raw === 'string' ? raw.trim() : raw;
      if (value === '' || value === undefined || value === null) {
        continue;
      }

      if (field.type === 'number') {
        const numericValue = Number(value);
        if (!Number.isFinite(numericValue)) {
          throw new Error(`${field.label} must be a number.`);
        }
        config[field.key] = numericValue;
      } else {
        config[field.key] = value;
      }
    }

    return { config, secrets };
  }

  function defaultConfigValues(fields: ConfigField[]): Record<string, string | boolean> {
    return Object.fromEntries(fields.map((field) => [field.key, field.type === 'checkbox' ? false : '']));
  }

  function serviceItems(definition: ProviderFormDefinition): SelectItem[] {
    return (definition.services ?? []).map((service) => ({ value: service.value, name: service.name }));
  }

  function providerDefinition(kind: string): ProviderFormDefinition {
    return providerDefinitions[kind] ?? { name: titleCase(kind), fields: [] };
  }

  function fieldInputType(field: ConfigField): string {
    if (field.type === 'number') return 'number';
    if (field.type === 'password') return 'password';
    return 'text';
  }

  function secretNameForField(fieldKey: string): string {
    return `${serviceProvider ? `${serviceProvider}.` : ''}${fieldKey}`;
  }

  function isRecord(value: unknown): value is Record<string, unknown> {
    return !!value && typeof value === 'object' && !Array.isArray(value);
  }

  function readRecord(value: unknown): Record<string, unknown> {
    return isRecord(value) ? value : {};
  }

  function readString(value: unknown): string {
    return typeof value === 'string' ? value : '';
  }

  function titleCase(value: string): string {
    return value
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/[-_]/g, ' ')
      .replace(/\b\w/g, (letter) => letter.toUpperCase());
  }

  function emailApiFields(): ConfigField[] {
    return [
      { key: 'apiKey', label: 'API key', type: 'password', secret: true },
      { key: 'fromEmail', label: 'From email', placeholder: 'froststream@example.com' },
      { key: 'fromName', label: 'From name', placeholder: 'FrostStream' }
    ];
  }
</script>

<form onsubmit={saveProvider} class="space-y-5">
  <div class="grid gap-4 md:grid-cols-2">
    <div>
      <label class="label mb-2 text-sm" for="notification-provider-key">Provider key</label>
      <input class="input w-full" id="notification-provider-key" required
         pattern={'[a-z0-9-]{2,100}'} minlength={2} maxlength={100} disabled={isUpdate} bind:value={providerKey} placeholder="alerts" />
      <p class="mt-1.5 text-xs text-base-content/40">Lowercase letters, numbers, and hyphens.</p>
    </div>
    <div>
      <label class="label mb-2 text-sm" for="notification-provider-kind">Provider kind</label>
      <Select id="notification-provider-kind" items={providerKindItems} bind:value={providerKind} onchange={onProviderKindChange} class="text-sm" />
    </div>
  </div>

  <div class="grid gap-4 md:grid-cols-2">
    <div>
      <label class="label mb-2 text-sm" for="notification-display-name">Display name</label>
      <input class="input w-full" id="notification-display-name" maxlength={255} bind:value={displayName} placeholder="Operations alerts" />
    </div>
    <div>
      <label class="label mb-2 text-sm" for="notification-default-to">Default recipient</label>
      <input class="input w-full" id="notification-default-to" maxlength={512} bind:value={defaultTo} placeholder="#ops-alerts" />
    </div>
  </div>

  <div class="border-t border-base-300/70 pt-5">
    <div class="flex items-center gap-2">
      <Bell class="h-4 w-4 text-primary" />
      <h3 class="text-sm font-bold text-base-content">{activeDefinition.name} settings</h3>
    </div>

    {#if activeDefinition.services}
      <div class="mt-4 max-w-sm">
        <label class="label mb-2 text-sm" for="notification-service-provider">
          {activeDefinition.serviceLabel ?? 'Service'}
        </label>
        <Select id="notification-service-provider" items={serviceItems(activeDefinition)} bind:value={serviceProvider} onchange={onServiceProviderChange} class="text-sm" />
      </div>
    {/if}

    <div class="mt-4 grid gap-4 md:grid-cols-2">
      {#each activeFields as field (field.key)}
        {#if field.type === 'checkbox'}
          <div class="flex min-h-10 items-center pt-7">
            <label class="label inline-flex cursor-pointer items-center gap-2"><input type="checkbox" class="checkbox" checked={configValues[field.key] === true} onchange={(event) => updateCheckboxValue(field.key, event.currentTarget.checked)} /><span>{field.label}</span></label>
          </div>
        {:else if field.type === 'textarea'}
          <div class="md:col-span-2">
            <label class="label mb-2 text-sm" for={`notification-field-${field.key}`}>
              {field.label}
            </label>
            <textarea class="textarea w-full text-sm" id={`notification-field-${field.key}`} rows={field.secret ? 4 : 5} value={field.secret ? (secretValues[field.key] ?? '') : String(configValues[field.key] ?? '')} oninput={(event) => {
                if (field.secret) {
                  updateSecretValue(field.key, inputValue(event));
                } else {
                  updateConfigValue(field.key, inputValue(event));
                }
              }} placeholder={field.secret && preservedSecretRefs[field.key] ? 'Stored secret is already configured' : field.placeholder}></textarea>
            {#if field.secret}
              <p class="mt-1.5 text-xs text-base-content/40">
                {preservedSecretRefs[field.key]
                  ? `Leave blank to keep ${preservedSecretRefs[field.key]}.`
                  : `Stored as secret://${providerKey.trim() || 'provider'}/${secretNameForField(field.key)}.`}
              </p>
            {/if}
          </div>
        {:else}
          <div>
            <label class="label mb-2 text-sm" for={`notification-field-${field.key}`}>
              {field.label}
            </label>
            <input class="input w-full" id={`notification-field-${field.key}`} type={fieldInputType(field)} value={field.secret ? (secretValues[field.key] ?? '') : String(configValues[field.key] ?? '')} placeholder={field.secret && preservedSecretRefs[field.key] ? 'Stored secret is already configured' : field.placeholder} oninput={(event) => {
                if (field.secret) {
                  updateSecretValue(field.key, inputValue(event));
                } else {
                  updateConfigValue(field.key, inputValue(event));
                }
              }} />
            {#if field.secret}
              <p class="mt-1.5 text-xs text-base-content/40">
                {preservedSecretRefs[field.key]
                  ? `Leave blank to keep ${preservedSecretRefs[field.key]}.`
                  : `Stored as secret://${providerKey.trim() || 'provider'}/${secretNameForField(field.key)}.`}
              </p>
            {/if}
          </div>
        {/if}
      {/each}
    </div>
  </div>

  {#if submitError}
    <div
      class="flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
      role="alert"
    >
      <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
      <span>{submitError}</span>
    </div>
  {/if}

  <div class="flex flex-col-reverse gap-3 border-t border-base-300/70 pt-5 sm:flex-row sm:items-center sm:justify-between">
    <label class="label inline-flex cursor-pointer items-center gap-2 text-sm"><input type="checkbox" class="checkbox" bind:checked={providerEnabled} /><span>Enabled</span></label>
    <div class="flex flex-col-reverse gap-3 sm:flex-row">
      <a class="btn btn-sm btn-ghost text-xs" href="/profile/notifications">
        <ArrowLeft class="mr-1.5 h-4 w-4" />
        Back
      </a>
      <button class="btn btn-sm btn-primary text-xs" type="submit" disabled={savingProvider}>
        {#if savingProvider}
          <span class="loading loading-spinner loading-xs mr-2"></span>
        {:else if isUpdate}
          <CircleCheck class="mr-1.5 h-4 w-4" />
        {:else}
          <Plus class="mr-1.5 h-4 w-4" />
        {/if}
        {isUpdate ? 'Save changes' : 'Create provider'}
      </button>
    </div>
  </div>
</form>

{#if isUpdate && initial}
  <section class="mt-5 rounded-xl border border-base-300/80 bg-base-200/20 p-4" aria-labelledby="notification-test-title">
    <h3 id="notification-test-title" class="flex items-center gap-2 text-sm font-bold text-base-content">
      <FlaskConical class="h-4 w-4 text-primary" />
      Test delivery
    </h3>

    {#if testError}
      <div
        class="mt-4 flex items-start gap-2 rounded-xl border border-error/30 bg-error/10 p-3 text-sm text-error"
        role="alert"
      >
        <CircleAlert class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{testError}</span>
      </div>
    {/if}

    {#if testStatus}
      <div
        class="mt-4 flex items-start gap-2 rounded-xl border border-success/30 bg-success/10 p-3 text-sm text-success"
        role="status"
      >
        <CircleCheck class="mt-0.5 h-4 w-4 shrink-0" />
        <span>{testStatus}</span>
      </div>
    {/if}

    <div class="mt-4">
      <label class="label mb-2 text-sm" for="notification-test-subject">Subject</label>
      <input class="input w-full" id="notification-test-subject" bind:value={testSubject} />
    </div>
    <div class="mt-4">
      <label class="label mb-2 text-sm" for="notification-test-body">Body</label>
      <textarea class="textarea w-full text-sm" id="notification-test-body" rows={5} bind:value={testBody}></textarea>
    </div>

    <button class="btn btn-sm btn-primary mt-4 text-xs" type="button" disabled={testingProvider} onclick={sendTest}>
      {#if testingProvider}
        <span class="loading loading-spinner loading-xs mr-1.5"></span>
      {:else}
        <Send class="mr-1.5 h-4 w-4" />
      {/if}
      Send test
    </button>
  </section>
{/if}
