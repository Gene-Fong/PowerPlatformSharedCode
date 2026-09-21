import {
  Button,
  CrmContext,
  EmptyState,
  ExtensionPointApiActions,
  Link,
  Text,
} from '@hubspot/ui-extensions';
import { hubspot } from '@hubspot/ui-extensions';
import { useState } from 'react';

interface ExtensionProps {
  context: CrmContext;
  actions: ExtensionPointApiActions<'crm.record.tab'>;
}

hubspot.extend<'crm.record.tab'>(({ context, actions }: ExtensionProps) => (
  <Extension context={context} actions={actions} />
));

const Extension = ({ context, actions }: ExtensionProps) => {
  const userEmail = context?.user?.email ?? 'current HubSpot user';
  const loginUrl = 'https://app.hubspot.com/login';
  const reauthUrl = 'https://developers.hubspot.com/docs/guides/apps/working-with-oauth';
  const [isReconnecting, setIsReconnecting] = useState(false);

  const handleReconnect = () => {
    setIsReconnecting(true);
    actions.openIframeModal({
      url: loginUrl,
      title: 'HubSpot login',
      width: 'large',
    });

    window.setTimeout(() => {
      setIsReconnecting(false);
    }, 2500);
  };

  return (
    <EmptyState
      title="HubSpot login"
      layout="vertical"
      imageName="shield"
    >
      <Text>
        This custom MCP connector uses HubSpot OAuth. If the token expires or the
        connection is lost, sign in again to restore access to CRM tools.
      </Text>

      <Text>
        Signed in as: <strong>{userEmail}</strong>
      </Text>

      <Button variant="primary" onClick={handleReconnect} disabled={isReconnecting}>
        {isReconnecting ? 'Reconnecting…' : 'Reconnect HubSpot access'}
      </Button>

      <Text>
        Need setup help? Review the{' '}
        <Link href={reauthUrl}>OAuth setup guide</Link> and confirm the redirect
        URL in the HubSpot app settings.
      </Text>

      <Link href={loginUrl}>Open HubSpot login</Link>
    </EmptyState>
  );
};
