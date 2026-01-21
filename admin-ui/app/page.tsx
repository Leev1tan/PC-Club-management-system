'use client';

import { useState, useEffect } from 'react';

const API_BASE = process.env.NEXT_PUBLIC_API_BASE || 'http://localhost:5081';

interface Device {
  id: string;
  hostname: string;
  osVersion: string;
  agentVersion: string;
  lastSeenUtc: string | null;
  lastIp: string | null;
  status: string;
}

export default function Home() {
  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(true);

  const fetchDevices = async () => {
    try {
      const res = await fetch(`${API_BASE}/api/devices`);
      const data = await res.json();
      setDevices(data);
    } catch (err) {
      console.error('Failed to fetch devices:', err);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchDevices();
    const interval = setInterval(fetchDevices, 5000);
    return () => clearInterval(interval);
  }, []);

  const sendCommand = async (deviceId: string, type: string) => {
    try {
      await fetch(`${API_BASE}/api/devices/${deviceId}/commands`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type, payload: null }),
      });
      alert(`Command ${type} sent to ${deviceId}`);
    } catch (err) {
      alert(`Failed to send command: ${err}`);
    }
  };

  const startSession = async (deviceId: string) => {
    const minutes = prompt('Enter session duration (minutes):', '10');
    if (!minutes) return;
    try {
      await fetch(`${API_BASE}/api/sessions`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ deviceId, durationMinutes: parseInt(minutes, 10) }),
      });
      alert(`Session started for ${minutes} minutes`);
    } catch (err) {
      alert(`Failed to start session: ${err}`);
    }
  };

  if (loading) return <div className="p-8">Loading...</div>;

  return (
    <div className="min-h-screen bg-gray-100 p-8">
      <h1 className="text-3xl font-bold mb-6">Club Management - Devices</h1>
      <div className="grid gap-4">
        {devices.length === 0 && <p className="text-gray-600">No devices registered yet.</p>}
        {devices.map((device) => (
          <div key={device.id} className="bg-white rounded-lg shadow p-6">
            <div className="flex items-center justify-between">
              <div>
                <h2 className="text-xl font-semibold">{device.hostname}</h2>
                <p className="text-sm text-gray-600">
                  {device.osVersion} • Agent {device.agentVersion}
                </p>
                <p className="text-sm text-gray-500">
                  Last seen: {device.lastSeenUtc ? new Date(device.lastSeenUtc).toLocaleString() : 'Never'} • {device.lastIp || 'N/A'}
                </p>
                <span
                  className={`inline-block mt-2 px-2 py-1 text-xs font-semibold rounded ${
                    device.status === 'online' ? 'bg-green-100 text-green-800' : 'bg-gray-100 text-gray-800'
                  }`}
                >
                  {device.status}
                </span>
              </div>
              <div className="flex gap-2">
                <button
                  onClick={() => sendCommand(device.id, 'lock')}
                  className="px-4 py-2 bg-red-600 text-white rounded hover:bg-red-700"
                >
                  Lock
                </button>
                <button
                  onClick={() => sendCommand(device.id, 'unlock')}
                  className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700"
                >
                  Unlock
                </button>
                <button
                  onClick={() => sendCommand(device.id, 'restart')}
                  className="px-4 py-2 bg-yellow-600 text-white rounded hover:bg-yellow-700"
                >
                  Restart
                </button>
                <button
                  onClick={() => startSession(device.id)}
                  className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700"
                >
                  Start Session
                </button>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
